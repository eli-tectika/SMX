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

/// The Decision stage, driven through the pipeline runner: assembly is deterministic domain code
/// (DecisionAssembler), only the final-code PICK is an agent, and the stage then PARKS at `awaiting-VP` —
/// never `done`, because a proposal is not a signature and only the VP gate completes the stage.
///
/// The false pass this file exists to prevent is a Decision that LOOKS complete without the VP's word:
/// a stage that went `done` off the agent's own pick would be the agent signing the hard gate (Law 9).
public class DecisionDispatchTests
{
    private const string P = "p1";

    private static (PipelineRunner Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(), conclusions, 2), store, agents);
    }

    /// A FRESH object round-tripped through the real router, never the instance the test is still holding —
    /// the round trip has caught real serialization bugs here, and a stage that mutated the doc it was
    /// handed would otherwise look correct against an object the test still owns.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    /// A project whose Decision stage sits at <paramref name="decisionStatus"/> (default `pending`, the
    /// trigger condition). Cost is `done` — Cost finishing is what lands the CostDoc that triggers here.
    private static ProjectDoc Project(string decisionStatus = "pending")
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        p.Stages[Stages.Intake].Status = "done";
        p.Stages[Stages.Discovery].Status = "done";
        p.Stages[Stages.Regulatory].Status = "done";
        p.Stages[Stages.Matrix].Status = "done";
        p.Stages[Stages.Dosing].Status = "done";
        p.Stages[Stages.Decision].Status = decisionStatus;
        return p;
    }

    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
    };

    /// The candidate set the verdicts below belong to. Seeded because the runner walks EVERY stage: a
    /// project carrying dosing and cost but no candidates is not a state the journey can produce, and
    /// leaving it out would have Pool and Discovery run inside a Decision test.
    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new("bottle", "Zr", "f", "cas-zr", null, null, true, "A", "ok", [new Citation("catalog", "x", "t")]),
            new("bottle", "Y", "f", "cas-y", null, null, true, "A", "ok", [new Citation("catalog", "x", "t")]),
        ],
    };

    private static VerdictDoc Verdict(string cas, string element) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P, Cas = cas, ComponentId = "bottle",
        Element = element, Form = "f",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true, Determination = Determinations.Recommended, DeterminationReason = "ruled",
    };

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    /// One component, two recommended substances, ONE finalized code over both — the fake's default pick.
    private static DosingDoc Dosing() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t",
        Windows =
        [
            new PpmWindow("bottle", "cas-zr", "Zr", new Bound(10, "m", BoundKinds.Measured, 1.0),
                new Bound(1000, "e", BoundKinds.Estimate, 0.5), 450, 30),
            new PpmWindow("bottle", "cas-y", "Y", new Bound(8, "m", BoundKinds.Measured, 1.0),
                new Bound(800, "e", BoundKinds.Estimate, 0.5), 200, 25),
        ],
        Codes = [new MarkerCode("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "ratio 9:4")],
    };


    /// Everything TryDecideAsync resolves: the project (Decision at <paramref name="decisionStatus"/>),
    /// constraints, both verdicts, dosing (overridable — the duplicate-window test seeds a poisoned one)
    /// and the CostDoc the delivery below re-hands the dispatcher.
    private static async Task SeedAsync(InMemoryRecordStore store, string decisionStatus = "pending",
        DosingDoc? dosing = null)
    {
        await store.UpsertProjectAsync(Project(decisionStatus));
        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y"));
        await store.UpsertDosingAsync(dosing ?? Dosing());
    }

    private static StageState DecisionStage(InMemoryRecordStore store) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[Stages.Decision];

    // ---- the trigger -----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePipelineReachingDecision_RunsAssemblyPlusPick()
    {
        var (d, store, agents) = Sut();
        await SeedAsync(store);

        await d.RunAsync(P, default);

        var decision = await store.GetDecisionAsync(P);
        Assert.NotNull(decision);
        Assert.Equal(RecordIds.Decision(P), decision!.Id);
        var bottle = Assert.Single(decision.Components);
        // The deterministic assembly reached the doc: one row per RECOMMENDED substance, ordinal by CAS.
        Assert.Equal(new[] { "cas-y", "cas-zr" }, bottle.Rows.Select(r => r.Cas).ToArray());
        // The fake's pick (the one finalized code) landed as a PROPOSAL...
        Assert.NotNull(bottle.ProposedCode);
        Assert.Equal(Dosing().Codes.Single().RatioSignature, bottle.ProposedCode!.RatioSignature);
        Assert.Equal(new[] { "cas-zr", "cas-y" }, bottle.ProposedCode.MarkerCas);
        // ...and NEVER as a confirmation — that field is the VP's alone (Law 9).
        Assert.Null(bottle.ConfirmedCode);
        // Parked at the VP's door: awaiting-VP, NOT done — only the signed gate completes the stage.
        var stage = DecisionStage(store);
        Assert.Equal("done", stage.Status);
        Assert.Null(stage.Error);
        Assert.Equal(1, agents.DecisionCalls);
        // Counted like every other agent call: the dispatch-level pin that keeps TotalCalls exhaustive.
        Assert.Equal(1, agents.TotalCalls);
    }

    // ---- idempotency ------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondPass_IsIdempotent()
    {
        // A resume re-enters every stage. It must not re-run the pick: the first run parked the stage at
        // `awaiting-VP`, and the STAGE STATUS guard absorbs every later pass.
        var (d, store, agents) = Sut();
        await SeedAsync(store);

        await d.RunAsync(P, default);
        await d.RunAsync(P, default);   // a second pass

        Assert.Equal(1, agents.DecisionCalls);
        Assert.Single(store.Documents.OfType<DecisionDoc>());
        var stage = DecisionStage(store);
        Assert.Equal("done", stage.Status);
        Assert.Equal(1, stage.Attempts);   // the second pass never even entered the run
    }

    [Fact]
    public async Task Decision_GuardsOnStageStatus_NotWhetherADecisionDocExists()
    {
        // The stage says `awaiting-VP` but no DecisionDoc is on file — the one state where a status guard
        // and a doc-existence guard DIVERGE. A runner guarding on "does a DecisionDoc exist" would find
        // none and RE-RUN, re-proposing over a stage already parked at the VP's door. Only the status guard
        // skips. (Mirror of Cost_GuardsOnStageStatus_NotWhetherACostDocExists — the same lesson.)
        var (d, store, agents) = Sut();
        await SeedAsync(store, decisionStatus: "done");

        await d.RunAsync(P, default);

        Assert.Null(await store.GetDecisionAsync(P));   // Decision did NOT run — the status alone stopped it
        Assert.Equal(0, agents.TotalCalls);
        Assert.Equal("done", DecisionStage(store).Status);
    }

    // ---- failure surfaces -------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedPick_LandsNeedsReview()
    {
        var (d, store, agents) = Sut();
        await SeedAsync(store);
        agents.Decision = (_, _, _) =>
            Task.FromResult(AgentRunResult<DecisionDoc>.NeedsReview("no valid code"));

        await d.RunAsync(P, default);

        var stage = DecisionStage(store);
        Assert.Equal("needs-review", stage.Status);
        Assert.Equal("no valid code", stage.Error);
        Assert.Null(await store.GetDecisionAsync(P));   // nothing persisted — no doc that LOOKS decided

        // And a second pass does not re-run the failed pick: `needs-review` is not `pending`. (A
        // doc-existence guard would re-run here — no DecisionDoc was ever written.)
        await d.RunAsync(P, default);
        Assert.Equal(1, agents.DecisionCalls);
    }

    [Theory]
    [InlineData("dosing")]
    [InlineData("constraints")]
    public async Task Decision_RequiresItsInputs(string missing)
    {
        // Resolve-all-inputs-first, the TryDoseAsync discipline: a missing upstream doc runs NOTHING and
        // parks NOTHING — the stage stays `pending`, so the next pass runs it once the input lands.
        var (d, store, agents) = Sut();
        await store.UpsertProjectAsync(Project());
        if (missing != "constraints") await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        if (missing != "dosing") await store.UpsertDosingAsync(Dosing());
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y"));

        await d.RunAsync(P, default);

        Assert.Equal("pending", DecisionStage(store).Status);
        Assert.Equal(0, agents.TotalCalls);
        Assert.Null(await store.GetDecisionAsync(P));
    }

    [Fact]
    public async Task APreInvariantDuplicateWindow_FailsTheStage_WithNoAgentCall_AndNoPoisonLoop()
    {
        // AMENDMENT (Tasks-3-5 review). DosingAgent now refuses a duplicate (component, cas) window at the
        // boundary, but a DosingDoc persisted BEFORE that invariant can still carry one — and
        // DecisionAssembler.Assemble's ToDictionary throws ArgumentException on it. If Assemble ran OUTSIDE
        // the stage's own error handling it would escape the runner and halt the pipeline with nothing on
        // the record: stage stuck `pending`, no visible error. INSIDE it, the stage lands `failed` with the
        // error surfaced — §11's "nothing dies silently".
        var (d, store, agents) = Sut();
        var poisoned = Dosing();
        poisoned.Windows.Add(poisoned.Windows[0]);   // the pre-invariant persisted duplicate (bottle, cas-zr)
        await SeedAsync(store, dosing: poisoned);

        await d.RunAsync(P, default);

        var stage = DecisionStage(store);
        Assert.Equal("failed", stage.Status);
        Assert.Contains("same key", stage.Error!);   // the ToDictionary ArgumentException, surfaced verbatim
        Assert.Equal(0, agents.DecisionCalls);       // the assembly threw before any agent ran
        Assert.Null(await store.GetDecisionAsync(P));

        // NO poison loop: the second pass is a no-op because the status is no longer `pending`.
        await d.RunAsync(P, default);
        Assert.Equal("failed", DecisionStage(store).Status);
        Assert.Equal(1, DecisionStage(store).Attempts);
        Assert.Equal(0, agents.TotalCalls);
    }
}
