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

/// The Dosing stage, driven through the pipeline runner.
///
/// This file used to be about a signature: Dosing ran only behind the R.E.'s approved gate, and the false
/// pass it guarded was the stage right after the gate bypassing it. Two specs later that is all gone —
/// execution-core §8 stopped the gate being a pipeline precondition, and §16.4 deleted the gate itself.
///
/// What it is about NOW is the SET and the FLAG. Dosing doses exactly CompliantSet.Of (everything the agent
/// did not reject, minus anything the operator vetoed) — a vetoed substance reaching the ppm stage is the
/// remaining false pass — and a run that had to fall back to a number nobody measured stamps the DosingDoc
/// provisional, which is what refuses the order. "Flag, do not guess": a missing measurement or metal
/// loading names itself rather than letting the agent improvise a marker nobody can detect.
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

    private static SubstancePropertyDoc Loading(string cas, string element) => new()
    {
        Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = "form",
        MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    /// A project screened through Regulatory with a compliant set of exactly one (cas-ok recommended; cas-no
    /// operator-REJECTED), the floor's inputs on file, and the loading known — i.e. FULLY doseable. Only the
    /// two "gap" toggles and the verdicts vary between tests. The project's Dosing stage is left `pending`,
    /// which is the condition RunDosingAsync acts on (a re-opened stage re-enters through exactly the same
    /// door — see the re-entry test).
    private static async Task SeedAsync(
        InMemoryRecordStore store, InMemoryKnowledgeStore knowledge,
        bool withBackground = true, bool withLoading = true,
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
    public async Task DosingRuns_OverTheCompliantSetOnly()
    {
        // Dosing is handed ONLY cas-ok, never the operator-REJECTED cas-no. A vetoed substance reaching the
        // ppm/code stage would be a chemical the operator explicitly refused, dosed into a customer's
        // product — and with the regulatory gate gone this filter is the whole of what stops it.
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
    public async Task AProjectWithEveryInputOnFile_DosesAndIsNotProvisional()
    {
        // Was AnAssembledButUNSIGNEDProject_DosesPROVISIONALLY, which seeded a LOCKED regulatory gate to
        // prove the pipeline no longer waited for one. There is no gate to lock now (§16.4), so what is
        // left to assert is the flag's meaning: with the measurement and the loading both on file, nothing
        // here rests on a number nobody measured, and the dosing is NOT provisional. It is the control the
        // two gap tests below are measured against.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);

        await d.RunAsync(P, default);

        Assert.Equal(1, agents.DosingCalls);
        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.Equal("done", DosingStage(store).Status);
        Assert.False(dosing!.Provisional);
        Assert.Empty(dosing.ProvisionalReasons);
    }

    [Fact]
    public async Task Dosing_OverTheAgentsProposalAlone_IsNOTProvisional_BecauseThatIsNowTheNormalBasis()
    {
        // THE BUG THIS TEST EXISTS TO PREVENT, and it is the reason the regulatory gate could not simply be
        // deleted (spec §16.4). Nobody has ruled; the agent proposed. That is the state EVERY project is in
        // after an unattended run now that no gate writes determinations.
        //
        // If "rests on the agent's proposal" stayed a provisional reason, every dosing this system ever
        // produces would be flagged, POST /orders would refuse forever, and the app would look perfectly
        // healthy while quietly never letting anyone buy anything. The flag has to shrink to the GENUINE
        // data gap — an estimated floor with no physicist measurement — which the two tests below still pin.
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge, casOk: Proposed("cas-ok", "Zr", VerdictStatus.Pass));

        await d.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.False(dosing!.Provisional);
        Assert.Empty(dosing.ProvisionalReasons);
        Assert.Equal(1, agents.DosingCalls);   // it dosed, over a NON-empty set
    }

    [Fact]
    public async Task Dosing_OverAProposal_StillHandsTheAgentTheSubstance_AndStillExcludesAnOperatorVeto()
    {
        // The other half of the new CompliantSet rule: the agent's proposal is the default admission, and an
        // operator `rejected` is an OVERRIDE that always wins. cas-no is proposed IN by the agent and vetoed
        // by the operator — a substance reaching the ppm stage past a human's refusal is the false pass this
        // whole lane exists to refuse, and it must not come back through the proposal door.
        var (d, store, agents, knowledge) = Sut();
        var vetoed = Proposed("cas-no", "Ba", VerdictStatus.Pass);
        vetoed.Determination = Determinations.Rejected;
        vetoed.DeterminationReason = "the operator refused it";
        await SeedAsync(store, knowledge,
            casOk: Proposed("cas-ok", "Zr", VerdictStatus.Pass), casNo: vetoed);

        IReadOnlyList<VerdictDoc>? handed = null;
        agents.Dosing = (c, dosable, _, _, _) =>
        {
            handed = dosable;
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId, GeneratedAt = "2026-07-15T00:00:00Z",
            }));
        };

        await d.RunAsync(P, default);

        Assert.NotNull(handed);
        Assert.Equal("cas-ok", Assert.Single(handed!).Cas);
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
