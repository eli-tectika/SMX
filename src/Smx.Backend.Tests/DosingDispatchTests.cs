using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;
using Smx.Backend.Knowledge;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The Dosing stage, driven through the pipeline runner. The false pass this file exists to prevent is the
/// hard regulatory gate being bypassed by the stage right after it: Dosing runs only behind the OPERATOR'S
/// SIGNATURE, and emphatically not off the MatrixDoc — the matrix assembles on verdict COMPLETENESS, before
/// any signature, so a project can be fully assembled and fully doseable and still unsigned. The runner
/// reaching Dosing is not permission; the gate record is. Everything else here is "park, do not guess":
/// a missing measurement or a missing metal loading stops the stage rather than letting the agent improvise a
/// marker nobody can detect or a batch nobody dosed right.
public class DosingDispatchTests
{
    private const string P = "p1";

    private static (PipelineRunner Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents, InMemoryKnowledgeStore Knowledge) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        // The REAL knowledge store is passed into the optional trailing param — the production wiring that
        // this task defers (Orchestrator/Program.cs) is exactly this argument.
        return (new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(), conclusions, 2, knowledge: knowledge), store, agents, knowledge);
    }

    /// A FRESH object round-tripped through the real router, never the instance the test is still holding.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    private static ConstraintsDoc Constraints(bool withBackground = true) => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
        // The floor's two inputs: the physicist's measured background + the device LOD for Zr.
        MeasuredBackgrounds = withBackground ? [new("bottle", "Zr", 5.0, "ppm")] : [],
        Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]),
    };

    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new("bottle", "Zr", "neodecanoate", "cas-ok", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
            new("bottle", "Ba", "sulfate", "cas-no", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
        ],
    };

    private static VerdictDoc Verdict(string cas, string element, VerdictStatus status, bool reviewed, string? determination) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
        Cas = cas, ComponentId = "bottle", Element = element, Form = "form",
        Dimensions = [new("ElementGate", status, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = reviewed,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
    };

    private static GateDoc Gate(string status) => new()
    {
        Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
        Status = status, ApprovedAt = status == "approved" ? "2026-07-13T09:00:00.0000000+00:00" : null,
    };

    private static SubstancePropertyDoc Loading(string cas, string element) => new()
    {
        Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = "form",
        MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    /// A project screened through Regulatory with a compliant set of exactly one (cas-ok recommended; cas-no
    /// rejected), the floor's inputs on file, and the loading known — i.e. FULLY doseable. Only the gate
    /// status and the two "gap" toggles vary between tests. The project's Dosing stage is left `pending`,
    /// which is the condition RunDosingAsync acts on (a re-opened park re-enters through exactly the same
    /// door — see the re-entry test).
    private static async Task SeedAsync(
        InMemoryRecordStore store, InMemoryKnowledgeStore knowledge,
        string gateStatus = "approved", bool withBackground = true, bool withLoading = true,
        VerdictDoc? casNo = null)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "awaiting-RE"; // OnGateAsync flips this to done
        project.Stages[Stages.Matrix].Status = "done";
        // Dosing stays "pending".
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints(withBackground));
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict("cas-ok", "Zr", VerdictStatus.Pass, reviewed: true, Determinations.Recommended));
        await store.UpsertVerdictAsync(casNo ?? Verdict("cas-no", "Ba", VerdictStatus.Pass, reviewed: true, Determinations.Rejected));
        await store.UpsertGateAsync(Gate(gateStatus));
        if (withLoading) await knowledge.UpsertSubstancePropertyAsync(Loading("cas-ok", "Zr"));
    }

    private static StageState DosingStage(InMemoryRecordStore store) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[Stages.Dosing];

    // ---- the trigger -----------------------------------------------------------------------------------

    [Fact]
    public async Task BehindTheApprovedGate_DosingRuns_OverTheCompliantSetOnly()
    {
        // Behind a signed gate the runner reaches Dosing — and Dosing is handed ONLY the
        // operator-recommended substance (cas-ok), never the rejected one (cas-no). A
        // rejected substance reaching the ppm/code stage would be a chemical the operator refused, dosed into
        // a customer's product past the very gate that refused it.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);

        IReadOnlyList<VerdictDoc>? handed = null;
        agents.Dosing = (c, compliant, _, _, _) =>
        {
            handed = compliant;
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId, GeneratedAt = "2026-07-15T00:00:00Z",
            }));
        };

        await d.RunAsync(P, default);

        Assert.Equal(1, agents.DosingCalls);
        Assert.NotNull(handed);
        var only = Assert.Single(handed!);
        Assert.Equal("cas-ok", only.Cas);                       // the compliant set, and ONLY it
        Assert.DoesNotContain(handed!, v => v.Cas == "cas-no"); // the rejected substance is not dosed
        Assert.Equal("done", DosingStage(store).Status);
    }

    [Fact]
    public async Task AnAssembledButUNSIGNEDProject_DoesNotDose()
    {
        // THE GATE-BYPASS GUARD. The matrix assembles on verdict COMPLETENESS — the operator has determined a
        // few substances but has NOT signed the regulatory gate. The runner walks the whole pipeline
        // regardless, so if reaching Dosing were permission to dose, those substances would be dosed anyway:
        // the hard regulatory gate bypassed by the stage right after it. The state below is FULLY doseable
        // (floor inputs present, loading known) except for the one thing that matters — the signature.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, gateStatus: "locked");   // determined, NOT signed

        await d.RunAsync(P, default);

        Assert.Equal(0, agents.DosingCalls);
        Assert.Null(await store.GetDosingAsync(P));
        Assert.Equal("pending", DosingStage(store).Status);       // the stage never moved
    }

    // ---- park, do not guess ----------------------------------------------------------------------------

    [Fact]
    public async Task Dosing_REFUSES_WhenTheGateIsSignedButNoLongerArmable()
    {
        // Defense in depth. A GateDoc carries no binding to the verdicts it was signed over, so `approved`
        // alone is not proof the CURRENT analysis was reviewed. Here a fresh, unreviewed, FAILING verdict is
        // live under the existing signature (the race POST /approve vs. a late verdict). Dosing must NOT run:
        // it re-checks RegulatoryGate.Armable and parks for review.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge,
            casNo: Verdict("cas-no", "Ba", VerdictStatus.Fail, reviewed: false, determination: null));

        await d.RunAsync(P, default);

        Assert.Equal(0, agents.DosingCalls);
        Assert.Null(await store.GetDosingAsync(P));
        var stage = DosingStage(store);
        Assert.Equal("needs-review", stage.Status);
        Assert.Contains("unreviewed", stage.Error);
        Assert.Contains("cas-no", stage.Error);
    }

    [Fact]
    public async Task Dosing_ParksInAwaitingPhysics_WhenTheMeasuredBackgroundIsMissing()
    {
        // The detection floor is computed from a MEASUREMENT, and an absent one is not a zero. Without it the
        // floor cannot be targeted at the device that must read the marker — so Dosing parks awaiting-physics
        // and produces nothing, rather than running the agent on a floor it would have to invent.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, withBackground: false);

        await d.RunAsync(P, default);

        Assert.Equal(0, agents.DosingCalls);
        Assert.Null(await store.GetDosingAsync(P));
        var stage = DosingStage(store);
        Assert.Equal("awaiting-physics", stage.Status);
        Assert.Contains("Zr", stage.Error);   // the gap names the element the physicist must measure
    }

    [Fact]
    public async Task Dosing_ParksInAwaitingOperator_WhenAMetalLoadingIsUnknown()
    {
        // The order amount is the mass of COMPOUND to buy, which needs the element's mass fraction — a number
        // in no catalog we have. When it is unknown the stage parks awaiting-operator and names the CAS; it is
        // entered once and kept forever (cross-project knowledge layer). It is never assumed to be 1.0.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, withLoading: false);

        await d.RunAsync(P, default);

        Assert.Equal(0, agents.DosingCalls);
        Assert.Null(await store.GetDosingAsync(P));
        var stage = DosingStage(store);
        Assert.Equal("awaiting-operator", stage.Status);
        Assert.Contains("cas-ok", stage.Error);
        Assert.Contains("metal loading", stage.Error);
    }

    // ---- idempotency + the write ------------------------------------------------------------------------

    [Fact]
    public async Task Dosing_IsIdempotent_UnderASecondPass()
    {
        // A resume re-enters every stage. It must not re-run Dosing: the first run moved the stage to `done`,
        // and the status guard is what absorbs every later pass.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);

        await d.RunAsync(P, default);
        await d.RunAsync(P, default);   // a second pass

        Assert.Equal(1, agents.DosingCalls);
        Assert.Equal("done", DosingStage(store).Status);
    }

    [Fact]
    public async Task Dosing_WritesTheDoc_AndMarksTheStageDone()
    {
        // The happy path: the agent's DosingDoc lands on the bus and the stage reaches `done` with no error.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);

        agents.Dosing = (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
            Windows = [new PpmWindow("bottle", "cas-ok", "Zr",
                Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
                Upper: new Bound(900.0, "solubility", BoundKinds.Estimate, 0.4),
                RecommendedPpm: 450.0, QuantificationPpm: 35.0)],
            GeneratedAt = "2026-07-15T00:00:00Z",
        }));

        await d.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.Equal(RecordIds.Dosing(P), dosing!.Id);
        Assert.Equal("cas-ok", Assert.Single(dosing.Windows).Cas);
        var stage = DosingStage(store);
        Assert.Equal("done", stage.Status);
        Assert.Null(stage.Error);
    }

    // ---- re-entry --------------------------------------------------------------------------------------

    [Fact]
    public async Task ReOpeningTheStage_ReEntersDosing_OnTheNextPass()
    {
        // POST /dosing/loading records a loading and re-opens Dosing to `pending`. That re-open is the ONLY
        // thing that lets a parked (or completed) Dosing stage run again — the skip is keyed on the STATUS
        // precisely so this door exists. Without it the loading the operator just entered reaches nothing.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);
        await d.RunAsync(P, default);
        Assert.Equal(1, agents.DosingCalls);

        var project = (await store.GetProjectAsync(P))!;
        project.Stages[Stages.Dosing].Status = "pending";   // what POST /dosing/loading writes
        await store.UpsertProjectAsync(project);

        await d.RunAsync(P, default);

        Assert.Equal(2, agents.DosingCalls);
        Assert.Equal("done", DosingStage(store).Status);
    }
}
