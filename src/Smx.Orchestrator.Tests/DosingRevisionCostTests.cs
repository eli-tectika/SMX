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

/// The interaction between a DOSING revision and the deterministic COST audit — the one place no existing test
/// wired BOTH the knowledge store (so Dosing can resolve loadings) AND the catalog (so Cost can price) into a
/// single dispatcher, which is exactly the gap that hid two bugs:
///
///   (1) A Dosing revision that changes the codes' substance set left the Cost audit STALE. Cost's trigger
///       (OnDosingAsync) guards on the Cost stage being `pending`, and after the first run Cost is `done` — so
///       without resetting Cost the revised DosingDoc reached nothing, and Cost kept pricing the OLD
///       substances (missing the new one's price/risk flags, pricing a substance no longer ordered). That is
///       the "wrong but looks current" artifact class this system must never produce.
///   (2) The Dosing revision path skipped the gate/Armable re-check the first-run path (TryDoseAsync) enforces,
///       so a revision could regenerate dosing (and, after fix 1, re-price) behind a LOCKED gate.
public class DosingRevisionCostTests
{
    private const string P = "p1";
    private const string SeededCostGeneratedAt = "2020-01-01T00:00:00.0000000+00:00";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents,
                    InMemoryKnowledgeStore Knowledge, FakeCatalogLookup Catalog) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        var catalog = new FakeCatalogLookup();
        // BOTH optional trailing params wired: the knowledge store so a Dosing revision resolves loadings, and
        // the catalog so the re-triggered Cost run can actually price. This is the wiring no prior test had.
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

    // Backgrounds + LODs for Zr, Y AND Fe, so a revision that swaps Zr→Fe can still resolve every floor.
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

    // Three candidates, all in the bottle: Zr, Y, Fe. Every one is recommended below, so a code may name any.
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

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    private static PpmWindow Win(string cas, string element, double recommended) =>
        new("bottle", cas, element,
            Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
            Upper: new Bound(900.0, "solubility", BoundKinds.Estimate, 0.4),
            RecommendedPpm: recommended, QuantificationPpm: 20.0);

    /// The DosingDoc already on the bus BEFORE the revision: its code names (cas-zr, cas-y).
    private static DosingDoc DosingBefore() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Windows = [Win("cas-zr", "Zr", 450.0), Win("cas-y", "Y", 200.0)],
        Codes = [new MarkerCode("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "ratio 9:4")],
        GeneratedAt = "2026-07-15T09:00:00.0000000+00:00",
    };

    /// What the fake Dosing agent returns on the re-run: a DIFFERENT substance set — the code now names
    /// (cas-y, cas-fe). cas-zr is dropped; cas-fe is new.
    private static DosingDoc DosingAfter() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Windows = [Win("cas-y", "Y", 200.0), Win("cas-fe", "Fe", 150.0)],
        Codes = [new MarkerCode("bottle", [Marker("cas-y", "Y", 200.0), Marker("cas-fe", "Fe", 150.0)], "ratio 4:3")],
        GeneratedAt = "2026-07-15T11:00:00.0000000+00:00",
    };

    /// The Cost audit computed over the OLD code (cas-zr, cas-y), stamped at a distinctive early T0 so a
    /// "Cost re-ran" assertion can prove the stamp ADVANCED — not merely that a CostDoc exists.
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

    private static CatalogCard Card(string cas, string element, string supplier, string refId, string price, string pack) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    /// A project that has already been DOSED and COSTED: gate <paramref name="gateStatus"/>, three recommended
    /// substances, floors/loadings on file, and BOTH a DosingDoc (cas-zr, cas-y) and a CostDoc (cas-zr, cas-y)
    /// already on the bus, Cost `done`. This is the state a Dosing revision acts on.
    private static async Task SeedDosedAndCostedAsync(
        InMemoryRecordStore store, InMemoryKnowledgeStore knowledge, string gateStatus = "approved")
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "done";
        project.Stages[Stages.Matrix].Status = "done";
        project.Stages[Stages.Dosing].Status = "done";
        project.Stages[Stages.Cost].Status = "done";     // already costed — the guard OnDosingAsync trusts
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y"));
        await store.UpsertVerdictAsync(Verdict("cas-fe", "Fe"));
        await store.UpsertGateAsync(Gate(gateStatus));
        await store.UpsertDosingAsync(DosingBefore());
        await store.UpsertCostAsync(CostBefore());
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-zr", "Zr"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-y", "Y"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-fe", "Fe"));
    }

    private static RevisionDoc DosingRevision() => new()
    {
        Id = RecordIds.Revision(P, Stages.Dosing, "rev1"), ProjectId = P, Stage = Stages.Dosing,
        Target = "swap the Zr marker for Fe in the bottle code",
        Reason = "the client's line now runs a Zr-bearing colorant — move to Fe so the code stays readable",
        CreatedAt = "2026-07-15T10:00:00.0000000+00:00",
    };

    private static StageState Stage(InMemoryRecordStore store, string stage) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[stage];

    // ---- Finding 1: a composition-changing Dosing revision re-audits Cost --------------------------------

    [Fact]
    public async Task ReviseDosing_ResetsCost_SoItReAuditsTheREVISEDSubstanceSet()
    {
        // The bug: a Dosing revision changes the code's substances (cas-zr → cas-fe), but Cost stayed `done`
        // from the first run, so the revised DosingDoc never re-triggered Cost — it kept pricing cas-zr (no
        // longer ordered) and never priced cas-fe (now ordered). The fix resets Cost to `pending` as part of
        // the revision's persist, so the newly-persisted DosingDoc re-triggers OnDosingAsync over the NEW set.
        var (d, store, agents, knowledge, catalog) = Sut();
        await SeedDosedAndCostedAsync(store, knowledge);
        // Priced cards for all three — Zr is priced too, so the assertion that Cost does NOT audit it proves
        // the OLD substance was dropped, not merely that its lookup returned nothing.
        catalog.Returns("Zr", Card("cas-zr", "Zr", "Acme Chemicals", "cat-zr", "$66.00", "25 g"))
               .Returns("Y", Card("cas-y", "Y", "Beta Reagents", "cat-y", "$50.00", "25 g"))
               .Returns("Fe", Card("cas-fe", "Fe", "Gamma Metals", "cat-fe", "$10.00", "25 g"));

        agents.Dosing = (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(DosingAfter()));

        // 1) The revision re-runs Dosing, resets Cost to `pending`, and persists the new DosingDoc.
        await d.OnRecordChangedAsync(Delivered(DosingRevision()), default);
        Assert.Equal(RevisionStatus.Applied, Assert.Single(await store.GetRevisionsAsync(P)).Status);
        Assert.Equal("pending", Stage(store, Stages.Cost).Status);   // the reset is what re-arms Cost's trigger

        // 2) The persisted DosingDoc reaches the change feed — THIS is what re-triggers Cost.
        await d.OnRecordChangedAsync(Delivered((await store.GetDosingAsync(P))!), default);

        var cost = await store.GetCostAsync(P);
        Assert.NotNull(cost);
        // Cost re-ran over the NEW set: cas-y (kept) + cas-fe (new), and NOT cas-zr (no longer ordered).
        Assert.Equal(
            new[] { "cas-fe", "cas-y" },
            cost!.Substances.Select(s => s.Cas).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(cost.Substances, s => s.Cas == "cas-zr");
        Assert.Contains(cost.Substances, s => s.Cas == "cas-fe");
        // cas-fe was actually priced from the catalog — the new substance carries a real quote, not a stub.
        var fe = cost.Substances.Single(s => s.Cas == "cas-fe");
        Assert.Equal(0.40, fe.BestQuote!.UsdPerGram, 2);   // $10.00 / 25 g
        // The audit is FRESH — its stamp advanced past the seeded T0 (not the stale artifact left standing).
        Assert.NotEqual(SeededCostGeneratedAt, cost.GeneratedAt);
        Assert.True(DateTimeOffset.Parse(cost.GeneratedAt) > DateTimeOffset.Parse(SeededCostGeneratedAt));
        Assert.Equal("done", Stage(store, Stages.Cost).Status);
    }

    // ---- Finding 2: a Dosing revision behind an unsigned gate fails, cleanly -----------------------------

    [Fact]
    public async Task ReviseDosing_WithGateNotApproved_Fails_LeavesTheDosingDoc_AndDoesNotResetCost()
    {
        // The first-run path (TryDoseAsync) re-checks that the regulatory gate is signed before dosing. A
        // Regulatory revision can VOID that gate (lock it) before the operator re-signs. The Dosing revision
        // path must enforce the same invariant: it may not regenerate dosing (and, per Finding 1, re-price)
        // behind a locked gate. The revision fails cleanly — every fallible step runs before anything mutates
        // — so the DosingDoc is untouched, no conclusion is written, and Cost is NOT reset.
        var (d, store, agents, knowledge, catalog) = Sut();
        await SeedDosedAndCostedAsync(store, knowledge, gateStatus: "locked");   // gate no longer signed

        var agentRan = false;
        agents.Dosing = (c, _, _, _, _) => { agentRan = true; return Task.FromResult(AgentRunResult<DosingDoc>.Ok(DosingAfter())); };

        await d.OnRecordChangedAsync(Delivered(DosingRevision()), default);

        // The revision failed, cleanly, naming the unsigned gate — and never reached the agent.
        var failed = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("gate is not approved", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Null(failed.AppliedAt);
        Assert.False(agentRan);
        Assert.Equal(0, agents.DosingCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));

        // The analysis is UNTOUCHED: the DosingDoc is still the seeded one (cas-zr, cas-y)...
        var dosing = (await store.GetDosingAsync(P))!;
        Assert.Equal(
            new[] { "cas-y", "cas-zr" },
            dosing.Codes.SelectMany(x => x.Markers).Select(m => m.Cas).Distinct()
                  .OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // ...and Cost was NOT reset: the stage stays `done` and the seeded CostDoc stands, un-re-priced.
        Assert.Equal("done", Stage(store, Stages.Cost).Status);
        Assert.Equal(SeededCostGeneratedAt, (await store.GetCostAsync(P))!.GeneratedAt);
        Assert.Empty(catalog.Calls);
    }
}
