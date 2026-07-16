using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// Revise-with-reason for DECISION (Law 4, Task 15), plus the two review-mandated closures that ride it:
///
///   (a) a DOSING revision on a project parked `awaiting-VP` must reset Decision to `pending`, so the
///       re-priced CostDoc re-runs the pick over the NEW dosing — without the reset, TryDecideAsync's
///       status guard ABSORBS the fresh CostDoc and the stale proposal (over codes that no longer exist)
///       sits at the VP's door looking current: the false pass, one layer up.
///   (b) NEITHER a Dosing nor a Decision revision may touch a CLOSED project (VP gate approved). The
///       signature is history: a revision silently rewriting a SIGNED decision would put words under a
///       signature the VP never read.
///
/// The Plan-4 holistic lesson applies: these tests wire BOTH stores (knowledge + catalog) through ONE
/// dispatcher, because the bugs live in the interactions no single-stage fixture exercises.
public class DecisionRevisionTests
{
    private const string P = "p1";
    private const string SeededDecisionGeneratedAt = "2020-01-01T00:00:00.0000000+00:00";
    private const string SeededCostGeneratedAt = "2020-01-02T00:00:00.0000000+00:00";

    /// RatioSignature is DERIVED from the markers (never a stored field), so the tests read it off the
    /// records rather than hard-coding a string that could drift from the rendering.
    private static string StaleRatio => DosingBefore().Codes.Single().RatioSignature;
    private static string RevisedRatio => DosingAfter().Codes.Single().RatioSignature;

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents,
                    InMemoryKnowledgeStore Knowledge, FakeCatalogLookup Catalog) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        var catalog = new FakeCatalogLookup();
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2, knowledge, catalog), store, agents, knowledge, catalog);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object round-tripped through the real
    /// router, never the instance the test is still holding.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    // Backgrounds + LODs for Zr, Y AND Fe, so the cascade test's revision can swap Zr→Fe and still
    // resolve every floor.
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
        MeasuredBackgrounds =
        [
            new("bottle", "Zr", 5.0, "ppm"),
            new("bottle", "Y", 4.0, "ppm"),
            new("bottle", "Fe", 3.0, "ppm"),
        ],
        Device = new XrfDevice("Niton XL5",
        [
            new DeviceLod("Zr", 2.0, "ppm"),
            new DeviceLod("Y", 2.0, "ppm"),
            new DeviceLod("Fe", 2.0, "ppm"),
        ]),
    };

    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
            new("bottle", "Y", "oxide", "cas-y", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
            new("bottle", "Fe", "oxide", "cas-fe", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
        ],
    };

    private static VerdictDoc Verdict(string cas, string element) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
        Cas = cas, ComponentId = "bottle", Element = element, Form = "form",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true,
        Determination = Determinations.Recommended,
        DeterminationReason = "operator recommended",
    };

    private static GateDoc RegGate() => new()
    {
        Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
        Status = "approved", ApprovedAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    private static GateDoc Vp(string status) => new()
    {
        Id = RecordIds.Gate(P, GateTypes.Vp), ProjectId = P, GateType = GateTypes.Vp,
        Status = status,
        ApprovedAt = status == "approved" ? "2026-07-16T09:00:00.0000000+00:00" : null,
        Reason = status == "locked" ? "the VP said not yet" : null,
    };

    private static SubstancePropertyDoc Loading(string cas, string element) => new()
    {
        Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = "form",
        MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    private static PpmWindow Win(string cas, string element, double recommended) =>
        new("bottle", cas, element,
            Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
            Upper: new Bound(900.0, "solubility", BoundKinds.Estimate, 0.4),
            RecommendedPpm: recommended, QuantificationPpm: 20.0);

    /// The DosingDoc already on the bus BEFORE any revision: its one code names (cas-zr, cas-y).
    private static DosingDoc DosingBefore() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Windows = [Win("cas-zr", "Zr", 450.0), Win("cas-y", "Y", 200.0)],
        Codes = [new MarkerCode("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "the first pick")],
        GeneratedAt = "2026-07-15T09:00:00.0000000+00:00",
    };

    /// What the scripted Dosing agent returns on a re-run: a DIFFERENT substance set — the code now names
    /// (cas-y, cas-fe) under a new ratio. cas-zr is dropped; cas-fe is new.
    private static DosingDoc DosingAfter() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Windows = [Win("cas-y", "Y", 200.0), Win("cas-fe", "Fe", 150.0)],
        Codes = [new MarkerCode("bottle", [Marker("cas-y", "Y", 200.0), Marker("cas-fe", "Fe", 150.0)], "the re-pick")],
        GeneratedAt = "2026-07-15T11:00:00.0000000+00:00",
    };

    private static CostDoc CostBefore() => new()
    {
        Id = RecordIds.Cost(P), ProjectId = P,
        Substances =
        [
            new SupplierAudit("cas-zr", "Zr", ["Acme Chemicals"], null, "", []),
            new SupplierAudit("cas-y", "Y", ["Beta Reagents"], null, "", []),
        ],
        GeneratedAt = SeededCostGeneratedAt,
    };

    /// The DecisionDoc the operator is about to revise — the agent's STALE pick over the (cas-zr, cas-y)
    /// code, with a distinctive GeneratedAt so an "UNCHANGED" assertion proves content, not mere presence.
    private static DecisionDoc DecisionBefore(bool confirmed = false) => new()
    {
        Id = RecordIds.Decision(P), ProjectId = P,
        Components =
        [
            new ComponentDecision("bottle",
                Rows:
                [
                    new DecisionRow("cas-zr", "Zr", Determinations.Recommended, 450.0,
                        new ClearedCriteria(true, true, true),
                        new TraceRefs(RecordIds.Verdict(P, "cas-zr", "bottle"), RecordIds.Dosing(P), RecordIds.Cost(P))),
                ],
                ProposedCode: new ProposedCode(StaleRatio, ["cas-zr", "cas-y"], "the stale pick"),
                ConfirmedCode: confirmed ? StaleRatio : null,
                ConfirmedBy: confirmed ? "VP R&D" : null,
                ConfirmedReason: confirmed ? "codes reviewed" : null),
        ],
        Procurement = confirmed
            ? new ProcurementState { Status = ProcurementStatus.Released }
            : new ProcurementState(),
        GeneratedAt = SeededDecisionGeneratedAt,
    };

    private static CatalogCard Card(string cas, string element, string supplier, string refId, string price, string pack) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    /// A project that has been DECIDED and is parked at the VP's door: every stage `done` except Decision
    /// at `awaiting-VP`, the signed regulatory gate, dosing + cost + the stale DecisionDoc on the bus, and
    /// every dosing input resolvable (so a Dosing revision can actually re-run).
    private static async Task SeedAwaitingVpAsync(InMemoryRecordStore store, InMemoryKnowledgeStore knowledge)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in new[] { Stages.Intake, Stages.Discovery, Stages.Regulatory, Stages.Matrix, Stages.Dosing, Stages.Cost })
            project.Stages[s].Status = "done";
        project.Stages[Stages.Decision].Status = "awaiting-VP";
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y"));
        await store.UpsertVerdictAsync(Verdict("cas-fe", "Fe"));
        await store.UpsertGateAsync(RegGate());
        await store.UpsertDosingAsync(DosingBefore());
        await store.UpsertCostAsync(CostBefore());
        await store.UpsertDecisionAsync(DecisionBefore());
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-zr", "Zr"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-y", "Y"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-fe", "Fe"));
    }

    /// A CLOSED project: what the close dispatch leaves behind — Decision `done`, the VP gate `approved`,
    /// the confirmations stamped and procurement Released. History.
    private static async Task SeedClosedAsync(InMemoryRecordStore store, InMemoryKnowledgeStore knowledge)
    {
        await SeedAwaitingVpAsync(store, knowledge);
        var project = (await store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "done";
        await store.UpsertProjectAsync(project);
        await store.UpsertGateAsync(Vp("approved"));
        await store.UpsertDecisionAsync(DecisionBefore(confirmed: true));
    }

    private static RevisionDoc DecisionRevision(string reason) => new()
    {
        Id = RecordIds.Revision(P, Stages.Decision, "rev1"), ProjectId = P, Stage = Stages.Decision,
        Target = "the bottle final-code pick",
        Reason = reason,
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    private static RevisionDoc DosingRevision(string reason) => new()
    {
        Id = RecordIds.Revision(P, Stages.Dosing, "rev1"), ProjectId = P, Stage = Stages.Dosing,
        Target = "swap the Zr marker for Fe in the bottle code",
        Reason = reason,
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    private static RevisionDoc DiscoveryRevision(string reason) => new()
    {
        Id = RecordIds.Revision(P, Stages.Discovery, "rev1"), ProjectId = P, Stage = Stages.Discovery,
        Target = "the candidate set",
        Reason = reason,
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    private static RevisionDoc RegulatoryRevision(string reason) => new()
    {
        Id = RecordIds.Revision(P, Stages.Regulatory, "rev1"), ProjectId = P, Stage = Stages.Regulatory,
        Target = "the cas-zr verdict",
        Reason = reason,
        Cas = "cas-zr", ComponentId = "bottle",
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    private static StageState Stage(InMemoryRecordStore store, string stage) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[stage];

    // ---- Half 1: the Decision executor arm ---------------------------------------------------------------

    [Fact]
    public async Task ARevision_RerunsThePick_WithTheReasonInThePrompt()
    {
        // Law 4: the operator never hand-edits the pick — they tell the agent WHY, and the agent re-picks
        // WITH the directive. A re-run that dropped the reason would reproduce the same pick and silently
        // discard the instruction.
        const string reason = "project X already shipped this ratio to the same client — pick a distinct code";
        var (d, store, agents, knowledge, _) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);

        RevisionDoc? seenByAgent = null;
        agents.Decision = (assembled, dosing, rev) =>
        {
            seenByAgent = rev;
            return Task.FromResult(AgentRunResult<DecisionDoc>.Ok(new DecisionDoc
            {
                Id = RecordIds.Decision(P), ProjectId = P,
                Components = [.. assembled.Select(c => c with { ProposedCode = new ProposedCode(
                    StaleRatio, ["cas-zr", "cas-y"], "re-picked per the directive") })],
                GeneratedAt = "2026-07-16T11:00:00.0000000+00:00",
            }));
        };

        var revision = DecisionRevision(reason);
        await d.OnRecordChangedAsync(Delivered(revision), default);

        // The agent saw the directive — the revision rode into the prompt, not just a bare re-run.
        Assert.Equal(1, agents.DecisionCalls);
        Assert.NotNull(seenByAgent);
        Assert.Equal(reason, seenByAgent!.Reason);

        // The revision APPLIED and the re-run's output actually landed (fresh stamp, fresh rationale).
        var applied = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.NotNull(applied.AppliedAt);
        Assert.Null(applied.Error);
        var decision = (await store.GetDecisionAsync(P))!;
        Assert.NotEqual(SeededDecisionGeneratedAt, decision.GeneratedAt);
        Assert.Equal("re-picked per the directive", Assert.Single(decision.Components).ProposedCode!.Rationale);
    }

    [Fact]
    public async Task ARevision_WritesALearnedConclusion()
    {
        // The reason becomes a Learned Conclusion filed under the DECISION kind (code-derived from the
        // stage, never the agent's choice) — a future pick can find why this one was overridden.
        const string reason = "the client's sister brand uses a 9:4 Zr:Y ratio — too close to distinguish in the field";
        var (d, store, agents, knowledge, _) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);

        var revision = DecisionRevision(reason);
        await d.OnRecordChangedAsync(Delivered(revision), default);

        Assert.Equal(1, agents.ConclusionCalls);
        var conclusion = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Decision, revision.Id);
        Assert.NotNull(conclusion);
        Assert.Equal(KnowledgeKinds.Decision, conclusion!.Kind);
        Assert.Equal(KnowledgeIds.RevisionConclusion(KnowledgeKinds.Decision, revision.Id), conclusion.Id);
        Assert.Contains(reason, Assert.Single(conclusion.Provenance.Decisions));   // verbatim, code-owned

        var applied = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.Equal(conclusion.Id, applied.ConclusionId);
    }

    [Fact]
    public async Task ARevision_ReparksAtAwaitingVp_AndVoidsAnUnsignedVpGate()
    {
        // The stage was parked `awaiting-VP` with a LOCKED vp gate on file (a prior VP rejection). The
        // revision replaces the proposal and re-parks at the VP's door — and the locked gate STAYS locked:
        // nothing on this path may move a gate toward `approved` (Law 9), and locked is already the safe
        // state a void would produce.
        var (d, store, agents, knowledge, _) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);
        await store.UpsertGateAsync(Vp("locked"));

        await d.OnRecordChangedAsync(Delivered(DecisionRevision("pick a code the VP has not already refused")), default);

        Assert.Equal(RevisionStatus.Applied, Assert.Single(await store.GetRevisionsAsync(P)).Status);

        // A NEW proposal is on file (the fake default re-picks off the live dosing codes)...
        var decision = (await store.GetDecisionAsync(P))!;
        Assert.NotEqual(SeededDecisionGeneratedAt, decision.GeneratedAt);
        Assert.NotNull(Assert.Single(decision.Components).ProposedCode);
        Assert.Null(Assert.Single(decision.Components).ConfirmedCode);   // and it is a PROPOSAL, unsigned

        // ...the stage is re-parked at the VP's door...
        var stage = Stage(store, Stages.Decision);
        Assert.Equal("awaiting-VP", stage.Status);
        Assert.Null(stage.Error);

        // ...the unsigned vp gate is still locked, and the REGULATORY gate (which a Decision revision is
        // strictly downstream of) still stands with its signature.
        Assert.Equal("locked", (await store.GetGateAsync(P, GateTypes.Vp))!.Status);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
        Assert.Equal("done", Stage(store, Stages.Regulatory).Status);
    }

    [Fact]
    public async Task ARevision_AfterClose_IsRefused()
    {
        // Decision `done` + vp `approved` + procurement Released = a CLOSED project. The false pass here:
        // a revision silently rewriting a SIGNED decision — the signature would then cover words the VP
        // never read. The revision is refused outright; the DecisionDoc is byte-for-byte the signed one.
        var (d, store, agents, knowledge, _) = Sut();
        await SeedClosedAsync(store, knowledge);

        await d.OnRecordChangedAsync(Delivered(DecisionRevision("actually, swap the code")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed — the VP signature is history; revising a closed decision " +
                        "requires a new project", refused.Error);
        Assert.Null(refused.ConclusionId);
        Assert.Null(refused.AppliedAt);

        // Nothing re-ran and nothing was half-learned.
        Assert.Equal(0, agents.DecisionCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));

        // The signed record is UNCHANGED — content, confirmations, procurement, stage and gate alike.
        var decision = (await store.GetDecisionAsync(P))!;
        Assert.Equal(SeededDecisionGeneratedAt, decision.GeneratedAt);
        var comp = Assert.Single(decision.Components);
        Assert.Equal(StaleRatio, comp.ConfirmedCode);
        Assert.Equal("VP R&D", comp.ConfirmedBy);
        Assert.Equal(ProcurementStatus.Released, decision.Procurement.Status);
        Assert.Equal("done", Stage(store, Stages.Decision).Status);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    // ---- (a): a Dosing revision on an awaiting-VP project re-runs Decision over the NEW dosing ------------

    [Fact]
    public async Task ADosingRevision_OnAnAwaitingVpProject_EndsWithDecisionRePickedOverTheNewDosing()
    {
        // The cross-task stale-decision cascade: without the Decision reset, the fresh CostDoc redelivers,
        // TryDecideAsync's guard sees `awaiting-VP` and ABSORBS it — the stale proposal (over the OLD
        // (cas-zr, cas-y) code) keeps sitting at the VP's door over dosing that no longer contains it.
        var (d, store, agents, knowledge, catalog) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);
        catalog.Returns("Zr", Card("cas-zr", "Zr", "Acme Chemicals", "cat-zr", "$66.00", "25 g"))
               .Returns("Y", Card("cas-y", "Y", "Beta Reagents", "cat-y", "$50.00", "25 g"))
               .Returns("Fe", Card("cas-fe", "Fe", "Gamma Metals", "cat-fe", "$10.00", "25 g"));
        agents.Dosing = (_, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(DosingAfter()));

        // 1) The revision re-runs Dosing — and resets BOTH downstream stages: Cost re-arms its trigger,
        //    Decision re-arms its own, with the park error cleared.
        await d.OnRecordChangedAsync(Delivered(DosingRevision("swap Zr for Fe — the client's colorant now carries Zr")), default);
        Assert.Equal(RevisionStatus.Applied, Assert.Single(await store.GetRevisionsAsync(P)).Status);
        Assert.Equal("pending", Stage(store, Stages.Cost).Status);
        var decisionStage = Stage(store, Stages.Decision);
        Assert.Equal("pending", decisionStage.Status);
        Assert.Null(decisionStage.Error);

        // 2) The persisted DosingDoc reaches the change feed — Cost re-prices the NEW substance set.
        await d.OnRecordChangedAsync(Delivered((await store.GetDosingAsync(P))!), default);
        Assert.Equal("done", Stage(store, Stages.Cost).Status);

        // 3) The fresh CostDoc reaches the change feed — and THIS time Decision is `pending`, so the pick
        //    re-runs over the NEW dosing: the proposal names the (cas-y, cas-fe) code, not the stale one.
        await d.OnRecordChangedAsync(Delivered((await store.GetCostAsync(P))!), default);

        Assert.Equal(1, agents.DecisionCalls);
        var decision = (await store.GetDecisionAsync(P))!;
        Assert.NotEqual(SeededDecisionGeneratedAt, decision.GeneratedAt);
        var proposal = Assert.Single(decision.Components).ProposedCode;
        Assert.NotNull(proposal);
        Assert.Equal(RevisedRatio, proposal!.RatioSignature);                     // the NEW code
        Assert.NotEqual(StaleRatio, proposal.RatioSignature);                     // not the one the VP saw
        Assert.Equal(new[] { "cas-y", "cas-fe" }, proposal.MarkerCas);
        Assert.DoesNotContain("cas-zr", proposal.MarkerCas);                      // the stale one is GONE
        Assert.Equal("awaiting-VP", Stage(store, Stages.Decision).Status);        // re-parked, fresh
    }

    // ---- (b): a Dosing revision on a CLOSED project is refused outright -----------------------------------

    [Fact]
    public async Task ADosingRevision_AfterClose_IsRefused_NothingReRun_NothingRePriced()
    {
        // Today nothing stops a Dosing revision from re-pricing a closed project under its signed decision.
        // The VP gate's approval is the close; behind it everything is history.
        var (d, store, agents, knowledge, catalog) = Sut();
        await SeedClosedAsync(store, knowledge);

        await d.OnRecordChangedAsync(Delivered(DosingRevision("swap Zr for Fe")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed — the VP signature is history", refused.Error);
        Assert.Null(refused.ConclusionId);

        // Nothing re-ran, nothing re-priced, nothing half-learned.
        Assert.Equal(0, agents.DosingCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.Empty(catalog.Calls);
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));

        // The record is untouched: dosing, cost, decision, stages and gate all stand as signed.
        Assert.Equal(DosingBefore().GeneratedAt, (await store.GetDosingAsync(P))!.GeneratedAt);
        Assert.Equal(SeededCostGeneratedAt, (await store.GetCostAsync(P))!.GeneratedAt);
        Assert.Equal(SeededDecisionGeneratedAt, (await store.GetDecisionAsync(P))!.GeneratedAt);
        Assert.Equal("done", Stage(store, Stages.Cost).Status);
        Assert.Equal("done", Stage(store, Stages.Decision).Status);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    // ---- the closed refusal covers ALL FOUR arms, not just the two that grew up next to the gate ----------
    //
    // The signed DecisionDoc.TraceRefs cite the verdict/dosing/cost records BY ID, and the CandidatesDoc is
    // the set those verdicts screened. A Discovery or Regulatory revision on a closed project would re-run
    // the agent, replace a CITED record under the same id (an audit trail rewritten under a standing
    // signature), clear the R.E. determination, and VoidRegulatoryGateAsync would flip the approved
    // regulatory gate to `locked` — a CLOSED project reappearing on the dashboard, blocked on an R.E. who
    // already ruled. Both are refused outright, before the agent runs.

    [Fact]
    public async Task ADiscoveryRevision_AfterClose_IsRefused_AndTheCitedRecordsSurvive()
    {
        var (d, store, agents, knowledge, _) = Sut();
        await SeedClosedAsync(store, knowledge);

        await d.OnRecordChangedAsync(Delivered(DiscoveryRevision("try lanthanides instead")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed — the VP signature is history; revising a closed project " +
                        "requires a new project", refused.Error);
        Assert.Null(refused.ConclusionId);
        Assert.Null(refused.AppliedAt);

        // Nothing re-ran and nothing was half-learned.
        Assert.Equal(0, agents.DiscoveryCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));

        // The candidate set the signed decision's trace runs through is UNCHANGED...
        var candidates = (await store.GetCandidatesAsync(P))!;
        Assert.Equal(new[] { "cas-zr", "cas-y", "cas-fe" }, candidates.Substances.Select(s => s.Cas));

        // ...the approved regulatory gate was NOT voided and no stage flipped — the project stays closed.
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
        Assert.Equal("done", Stage(store, Stages.Discovery).Status);
        Assert.Equal("done", Stage(store, Stages.Regulatory).Status);
        Assert.Equal("done", Stage(store, Stages.Decision).Status);
        Assert.Equal(SeededDecisionGeneratedAt, (await store.GetDecisionAsync(P))!.GeneratedAt);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    [Fact]
    public async Task ARegulatoryRevision_AfterClose_IsRefused_AndTheDeterminationSurvives()
    {
        var (d, store, agents, knowledge, _) = Sut();
        await SeedClosedAsync(store, knowledge);

        await d.OnRecordChangedAsync(Delivered(RegulatoryRevision("re-screen cas-zr against the new annex")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed — the VP signature is history; revising a closed project " +
                        "requires a new project", refused.Error);
        Assert.Null(refused.ConclusionId);
        Assert.Null(refused.AppliedAt);

        // Nothing re-ran and nothing was half-learned.
        Assert.Equal(0, agents.RegulatoryCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));

        // The verdict the signed TraceRefs cite is UNCHANGED — the R.E. determination was NOT cleared...
        var verdict = (await store.GetVerdictAsync(P, "cas-zr", "bottle"))!;
        Assert.Equal(Determinations.Recommended, verdict.Determination);
        Assert.True(verdict.EvidenceReviewed);

        // ...the approved regulatory gate was NOT voided and no stage flipped — the project stays closed.
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
        Assert.Equal("done", Stage(store, Stages.Regulatory).Status);
        Assert.Equal("done", Stage(store, Stages.Decision).Status);
        Assert.Equal(SeededDecisionGeneratedAt, (await store.GetDecisionAsync(P))!.GeneratedAt);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    // ---- the race the (d) guard cannot see (Task 15 review F1, layer 2) -----------------------------------
    //
    // During the ENTIRE revise run (closed-check → decision LLM → conclusion LLM → embed → push → persist)
    // the stage still advertises `awaiting-VP`, so a determination STARTED before the revision landed can
    // complete mid-run (layer 3 blocks one started after — see the endpoint tests). The persist closures
    // therefore re-check the world immediately before writing: if the signature won the race, the revision
    // lands honest `failed` and persists NOTHING — the stamped doc survives.

    [Fact]
    public async Task ARevision_RacedByTheSignature_FailsAndTheStampedDocSurvives()
    {
        var (d, store, agents, knowledge, _) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);

        // The fake decision agent performs the VP's determination MID-CALL, exactly as the endpoint would:
        // Confirmed* stamped onto the doc ON FILE, the approved gate written. When the fake returns, the
        // revise run is between its entry checks (which passed — nothing was signed then) and its persist.
        agents.Decision = async (assembled, dosing, rev) =>
        {
            var onFile = (await store.GetDecisionAsync(P))!;
            onFile.Components = [.. onFile.Components.Select(x => x with
            {
                ConfirmedCode = StaleRatio, ConfirmedBy = "VP R&D", ConfirmedReason = "signed mid-revision",
            })];
            await store.UpsertDecisionAsync(onFile);
            await store.UpsertGateAsync(Vp("approved"));
            return AgentRunResult<DecisionDoc>.Ok(new DecisionDoc
            {
                Id = RecordIds.Decision(P), ProjectId = P,
                Components = [.. assembled.Select(c => c with { ProposedCode = new ProposedCode(
                    RevisedRatio, ["cas-y", "cas-fe"], "the re-pick that lost the race") })],
                GeneratedAt = "2026-07-16T11:00:00.0000000+00:00",
            });
        };

        await d.OnRecordChangedAsync(Delivered(DecisionRevision("re-pick — racing the pen")), default);

        // The revision lands honest FAILED — the persist re-check saw the signature and refused to write.
        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed", refused.Error);
        // The conclusion was already written when the race was detected (it runs before persist, by the
        // ordered-mutation contract) — the documented orphan, findable via the id the failed doc carries.
        Assert.NotNull(refused.ConclusionId);

        // The STAMPED doc survives byte-for-byte: the fresh unconfirmed doc never landed.
        var decision = (await store.GetDecisionAsync(P))!;
        var comp = Assert.Single(decision.Components);
        Assert.Equal(StaleRatio, comp.ConfirmedCode);
        Assert.Equal("signed mid-revision", comp.ConfirmedReason);
        Assert.Equal(SeededDecisionGeneratedAt, decision.GeneratedAt);
        Assert.Equal("awaiting-VP", Stage(store, Stages.Decision).Status);   // untouched by the failed persist

        // ...so when the already-written approved gate now delivers, the close proceeds over the SIGNED
        // doc — confirmations intact, procurement released over a real conclusion, never over nothing.
        await d.OnRecordChangedAsync(Delivered((await store.GetGateAsync(P, GateTypes.Vp))!), default);
        Assert.Equal("done", Stage(store, Stages.Decision).Status);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Equal(StaleRatio, Assert.Single((await store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Fact]
    public async Task ADosingRevision_RacedByTheSignature_FailsAndResetsNothing()
    {
        // The same race through the OTHER revise path: a determination completes while the dosing re-run
        // is in flight. Without the persist re-check, the closure would reset Cost AND Decision to
        // `pending` and upsert the new DosingDoc — the whole cascade re-running underneath a just-signed
        // gate, regenerating the records the signature covers.
        var (d, store, agents, knowledge, catalog) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);
        agents.Dosing = async (c, _, _, _, _) =>
        {
            var onFile = (await store.GetDecisionAsync(P))!;
            onFile.Components = [.. onFile.Components.Select(x => x with
            {
                ConfirmedCode = StaleRatio, ConfirmedBy = "VP R&D", ConfirmedReason = "signed mid-revision",
            })];
            await store.UpsertDecisionAsync(onFile);
            await store.UpsertGateAsync(Vp("approved"));
            return AgentRunResult<DosingDoc>.Ok(DosingAfter());
        };

        await d.OnRecordChangedAsync(Delivered(DosingRevision("swap Zr for Fe — racing the pen")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("the project is closed", refused.Error);

        // NOTHING was reset and NOTHING persisted: dosing/cost stand, both downstream stages untouched.
        Assert.Equal(DosingBefore().GeneratedAt, (await store.GetDosingAsync(P))!.GeneratedAt);
        Assert.Equal(SeededCostGeneratedAt, (await store.GetCostAsync(P))!.GeneratedAt);
        Assert.Equal("done", Stage(store, Stages.Cost).Status);
        Assert.Equal("awaiting-VP", Stage(store, Stages.Decision).Status);   // NOT reset to pending
        Assert.Empty(catalog.Calls);
        Assert.Equal(StaleRatio, Assert.Single((await store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Fact]
    public async Task ARevision_WhoseStageMovedMidRun_FailsWithoutPersisting()
    {
        // The status half of the persist re-check: no signature this time, but the Decision stage moved
        // mid-run (a concurrent Dosing revision's reset landing while the pick was re-running). Persisting
        // would re-park `awaiting-VP` with a doc assembled over dosing that is being replaced — a stale
        // proposal advertised as fresh. The revision must fail without writing.
        var (d, store, agents, knowledge, _) = Sut();
        await SeedAwaitingVpAsync(store, knowledge);
        agents.Decision = async (assembled, dosing, rev) =>
        {
            var p = (await store.GetProjectAsync(P))!;
            p.Stages[Stages.Decision].Status = "pending";   // the concurrent reset lands mid-call
            await store.UpsertProjectAsync(p);
            return AgentRunResult<DecisionDoc>.Ok(new DecisionDoc
            {
                Id = RecordIds.Decision(P), ProjectId = P,
                Components = [.. assembled.Select(c => c with { ProposedCode = new ProposedCode(
                    StaleRatio, ["cas-zr", "cas-y"], "assembled over dosing that is being replaced") })],
                GeneratedAt = "2026-07-16T11:00:00.0000000+00:00",
            });
        };

        await d.OnRecordChangedAsync(Delivered(DecisionRevision("re-pick")), default);

        var refused = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, refused.Status);
        Assert.Contains("moved", refused.Error);
        // Nothing persisted: the seeded doc stands, and the stage keeps the status the CONCURRENT actor
        // gave it — this path must not re-park a stage it no longer understands.
        Assert.Equal(SeededDecisionGeneratedAt, (await store.GetDecisionAsync(P))!.GeneratedAt);
        Assert.Equal("pending", Stage(store, Stages.Decision).Status);
    }

    // ---- the front door ----------------------------------------------------------------------------------

    [Fact]
    public void ReviseDecision_IsRoutedByTheEndpointBecauseIsRevisableIsTrue()
    {
        // POST /revise and the chat apply_revision tool both route on RevisionEffects.IsRevisable(stage) —
        // the property IS the front door; the executor arm above is what stops it opening onto a throw.
        Assert.True(RevisionEffects.IsRevisable(Stages.Decision));

        // And the gate-void decision this stage routes to is the SAFE one — a Decision revision is strictly
        // downstream of the regulatory signature and must not void it.
        Assert.False(RevisionEffects.BreaksRegulatoryGate(Stages.Decision));
    }
}
