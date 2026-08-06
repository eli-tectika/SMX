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
        VerdictDoc? casNo = null, VerdictDoc? casOk = null)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "done";  // lands done with its verdicts (execution-core §8)
        project.Stages[Stages.Matrix].Status = "done";
        // Dosing stays "pending".
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints(withBackground));
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(casOk ?? Verdict("cas-ok", "Zr", VerdictStatus.Pass, reviewed: true, Determinations.Recommended));
        await store.UpsertVerdictAsync(casNo ?? Verdict("cas-no", "Ba", VerdictStatus.Pass, reviewed: true, Determinations.Rejected));
        await store.UpsertGateAsync(Gate(gateStatus));
        if (withLoading) await knowledge.UpsertSubstancePropertyAsync(Loading("cas-ok", "Zr"));
    }

    /// A verdict nobody has ruled on, carrying only the AGENT'S proposal. The state every project is in
    /// immediately after Regulatory now that the pipeline does not park (spec §10.1).
    private static VerdictDoc Proposed(string cas, string element, VerdictStatus status)
    {
        var v = Verdict(cas, element, status, reviewed: true, determination: null);
        v.ProposedDetermination = Determinations.Recommended;
        v.ProposedReason = "the agent's proposal";
        return v;
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
    public async Task AnAssembledButUNSIGNEDProject_DosesPROVISIONALLY()
    {
        // Was AnAssembledButUNSIGNEDProject_DoesNotDose. Execution-core §8/D10 removed the gate as a pipeline
        // precondition: the operator sees a complete proposed answer in one sitting. What replaces the old
        // guard is NOT weaker, it just sits somewhere else — the dosing is stamped PROVISIONAL, and
        // procurement refuses over that flag (UnattendedRunTests). The gate still governs the two
        // irreversible acts; it no longer governs whether agents may run.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, gateStatus: "locked");   // determined, NOT signed

        await d.RunAsync(P, default);

        Assert.Equal(1, agents.DosingCalls);
        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.Equal("done", DosingStage(store).Status);

        // The seeded verdict carries an OPERATOR determination, so nothing here rests on a proposal and the
        // dosing is NOT provisional on that account. This is the case that proves the flag tracks evidence
        // quality rather than merely "is the gate signed".
        Assert.False(dosing!.Provisional);
    }

    [Fact]
    public async Task Dosing_IsProvisional_WhenASubstanceRestsOnTheAgentsProposalAlone()
    {
        // Spec §10.1, the whole reason ProvisionalSet exists. Nobody has ruled; the agent proposed. Dosing
        // runs (otherwise the operator would get an empty answer that looked finished) and says so.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, gateStatus: "locked",
            casOk: Proposed("cas-ok", "Zr", VerdictStatus.Pass));

        await d.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.True(dosing!.Provisional);
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("proposal alone"));
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("cas-ok"));
    }

    // ---- flag, do not guess ----------------------------------------------------------------------------

    [Fact]
    public async Task Dosing_UsesTheDefaultFloor_AndFlagsIt_WhenTheMeasuredBackgroundIsMissing()
    {
        // Was Dosing_ParksInAwaitingPhysics_*. The floor is still never INVENTED — DetectionFloor still
        // refuses — but the caller now falls back to the device's generic limit of detection and flags the
        // component (§8: "estimated floor — no physicist measurement on file"). The flag blocks the ORDER,
        // not the pipeline.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, withBackground: false);

        await d.RunAsync(P, default);

        Assert.Equal(1, agents.DosingCalls);
        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.Equal("done", DosingStage(store).Status);
        Assert.True(dosing!.Provisional);
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("no physicist measurement"));
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("Zr"));
    }

    [Fact]
    public async Task Dosing_DropsTheSubstance_AndFlagsIt_WhenAMetalLoadingIsUnknown()
    {
        // Was Dosing_ParksInAwaitingOperator_*. The mass fraction still cannot be defaulted — an absent
        // loading is not 1.0, which would under-order an oxide — so the substance is DROPPED from this run
        // and named. What changed is that it no longer stops the run for everything else.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, withLoading: false);

        await d.RunAsync(P, default);

        var stage = DosingStage(store);
        // Every dosable substance was dropped here, so there was nothing left to dose at all.
        Assert.Equal(0, agents.DosingCalls);
        Assert.Equal("needs-review", stage.Status);
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
