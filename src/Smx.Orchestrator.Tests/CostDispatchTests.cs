using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Tools;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The Cost dispatch, end to end through the bus. Cost is DETERMINISTIC — no agent (§3.4): when Dosing
/// finishes, the finalized codes name the substances that will actually be ORDERED, and Cost audits exactly
/// those against the ref-catalog. The DosingDoc landing on the change feed IS the trigger.
///
/// The false pass this file exists to prevent is the SOFT code-finalization checkpoint (POST /dosing/review)
/// silently re-pricing the whole project. That checkpoint upserts the SAME DosingDoc to record a review note,
/// which re-delivers here — so the guard MUST be the Cost STAGE STATUS ("has Cost run?"), never the mere
/// existence of the DosingDoc. `ASoftReviewNote_DoesNotReRunCost` is the pin for that.
public class CostDispatchTests
{
    private const string P = "p1";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents, FakeCatalogLookup Catalog) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var catalog = new FakeCatalogLookup();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        // The FakeCatalogLookup is passed into the OPTIONAL trailing param — the production wiring this task
        // defers (Orchestrator/Program.cs must pass the real ICatalogLookup as the 6th arg) is exactly this.
        return (new StageDispatcher(store, agents, conclusions, 2, catalog: catalog), store, agents, catalog);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object round-tripped through the real
    /// router, never the instance the test is still holding. A handler that mutated the fed object would make
    /// a "stale redelivery" no longer stale, hiding an idempotency bug.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    private static CatalogCard Card(string cas, string element, string supplier, string refId,
        string? price = null, string? pack = null) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    /// A project whose Cost stage sits at <paramref name="costStatus"/> (default `pending`, the trigger
    /// condition). Only the Cost stage matters here — OnDosingAsync reads nothing else off the project.
    private static ProjectDoc Project(string costStatus = "pending")
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        p.Stages[Stages.Intake].Status = "done";
        p.Stages[Stages.Discovery].Status = "done";
        p.Stages[Stages.Regulatory].Status = "done";
        p.Stages[Stages.Matrix].Status = "done";
        p.Stages[Stages.Dosing].Status = "done";     // Dosing finished — that is what triggers Cost
        p.Stages[Stages.Cost].Status = costStatus;
        return p;
    }

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    /// A DosingDoc whose finalized codes name (cas-zr, Zr) and (cas-y, Y) — deliberately across TWO components
    /// so the same substances appear twice. Cost must DISTINCT them: two audited rows, not four.
    private static DosingDoc Dosing(string? reviewNote = null) => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Codes =
        [
            new MarkerCode("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "ratio 9:4"),
            new MarkerCode("label",  [Marker("cas-zr", "Zr", 300.0), Marker("cas-y", "Y", 150.0)], "ratio 2:1"),
        ],
        ReviewNote = reviewNote,
        ReviewedAt = reviewNote is null ? null : "2026-07-15T12:00:00.0000000+00:00",
        GeneratedAt = "2026-07-15T00:00:00.0000000+00:00",
    };

    /// A priced catalog for both substances the codes name: Zr from one supplier, Y from another.
    private static FakeCatalogLookup PricedCatalog(FakeCatalogLookup c) => c
        .Returns("Zr", Card("cas-zr", "Zr", "Acme Chemicals", "cat-zr-1", "$66.00", "25 g"))
        .Returns("Y",  Card("cas-y",  "Y",  "Beta Reagents",  "cat-y-1",  "$50.00", "25 g"));

    private static StageState CostStage(InMemoryRecordStore store) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[Stages.Cost];

    // ---- the trigger -----------------------------------------------------------------------------------

    [Fact]
    public async Task Dosing_TriggersCost_OverTheSubstancesInTheFINALISEDCodes()
    {
        // The finalized codes name what will actually be ordered, so Cost audits EXACTLY those substances —
        // the distinct (CAS, element) pairs drawn from the codes' markers, nothing more, nothing less.
        var (d, store, _, catalog) = Sut();
        PricedCatalog(catalog);
        await store.UpsertProjectAsync(Project());

        await d.OnRecordChangedAsync(Delivered(Dosing()), default);

        var cost = await store.GetCostAsync(P);
        Assert.NotNull(cost);
        // The audited substances are EXACTLY the codes' CAS — de-duplicated across the two components.
        Assert.Equal(
            new[] { "cas-y", "cas-zr" },
            cost!.Substances.Select(s => s.Cas).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // DISTINCT is real: two components each name both substances (four markers), but Cost prices two rows
        // and reads two catalog partitions — not four.
        Assert.Equal(2, cost.Substances.Count);
        Assert.Equal(2, catalog.Calls.Count);
        // And it actually priced them from the catalog — not a fabricated number.
        var zr = cost.Substances.Single(s => s.Cas == "cas-zr");
        Assert.Equal(2.64, zr.BestQuote!.UsdPerGram, 2);   // $66.00 / 25 g
        Assert.StartsWith("ref-catalog/", zr.BestQuote.Citation.Reference);
        Assert.Equal("done", CostStage(store).Status);
    }

    [Fact]
    public async Task Cost_RunsWithNoAgent_AtAll()
    {
        // §3.4: Cost is DETERMINISTIC. Not one agent arm is touched — if Cost ever needs a model, that is a
        // design change to argue for in the open, not one that slips in behind a green suite.
        var (d, store, agents, catalog) = Sut();
        PricedCatalog(catalog);
        await store.UpsertProjectAsync(Project());

        await d.OnRecordChangedAsync(Delivered(Dosing()), default);

        Assert.Equal(0, agents.TotalCalls);
        Assert.Equal(0, agents.IntakeCalls);
        Assert.Equal(0, agents.DiscoveryCalls);
        Assert.Equal(0, agents.RegulatoryCalls);
        Assert.Equal(0, agents.DosingCalls);
        Assert.Equal(0, agents.ChatCalls);
        Assert.Equal(0, agents.ConclusionCalls);
        Assert.NotNull(await store.GetCostAsync(P));   // it DID run — just without an agent
    }

    // ---- idempotency ------------------------------------------------------------------------------------

    [Fact]
    public async Task Cost_IsIdempotent_UnderChangeFeedRedelivery()
    {
        // The change feed is at-least-once. A redelivered DosingDoc must not re-run Cost: the first run moved
        // the stage to `done`, and the `pending` guard is what absorbs every later delivery. Proven by a
        // GeneratedAt that does NOT advance and a catalog that is NOT read a second time.
        var (d, store, _, catalog) = Sut();
        PricedCatalog(catalog);
        await store.UpsertProjectAsync(Project());

        await d.OnRecordChangedAsync(Delivered(Dosing()), default);
        var t0 = (await store.GetCostAsync(P))!.GeneratedAt;
        var callsAfterFirst = catalog.Calls.Count;

        await d.OnRecordChangedAsync(Delivered(Dosing()), default);   // redelivery

        Assert.Equal(t0, (await store.GetCostAsync(P))!.GeneratedAt);  // not re-stamped
        Assert.Equal(callsAfterFirst, catalog.Calls.Count);           // not re-priced
        Assert.Equal("done", CostStage(store).Status);
    }

    [Fact]
    public async Task ASoftReviewNote_DoesNotReRunCost()
    {
        // THE ONE TO THINK HARDEST ABOUT. Cost has already run (stage `done`, a CostDoc stamped at T0). The
        // operator now records a soft code-finalization review note — POST /dosing/review upserts the SAME
        // DosingDoc, and the feed re-delivers it here. Cost must NOT re-run: it re-prices nothing, the CostDoc
        // keeps its T0 stamp.
        //
        // This is what proves the guard is the STAGE STATUS, not the doc's existence: only a status guard skips
        // here. (Mutate the guard to run-whenever-a-DosingDoc-arrives and T0 advances — the note re-prices the
        // whole project, exactly the false pass this pins.) The assertion is GeneratedAt EQUALITY against the
        // seeded T0, not "a CostDoc exists" — a re-run would leave a CostDoc too, just with a fresh stamp.
        var (d, store, _, catalog) = Sut();
        PricedCatalog(catalog);
        await store.UpsertProjectAsync(Project(costStatus: "done"));   // Cost already ran

        const string T0 = "2020-01-01T00:00:00.0000000+00:00";
        await store.UpsertCostAsync(new CostDoc
        {
            Id = RecordIds.Cost(P), ProjectId = P,
            Substances = [new SupplierAudit("cas-zr", "Zr", ["Acme Chemicals"], null, "", [])],
            GeneratedAt = T0,
        });

        await d.OnRecordChangedAsync(Delivered(Dosing(reviewNote: "PL + VP reviewed the codes 2026-07-15")), default);

        var cost = await store.GetCostAsync(P);
        Assert.NotNull(cost);
        Assert.Equal(T0, cost!.GeneratedAt);          // NOT re-run — the stamp is exactly the seeded one
        Assert.Empty(catalog.Calls);                  // the catalog was never read: Cost never ran
        Assert.Equal("done", CostStage(store).Status);
    }

    [Fact]
    public async Task Cost_GuardsOnStageStatus_NotWhetherACostDocExists()
    {
        // The guard that decides whether to run Cost is the Cost STAGE STATUS — "has this stage run?" — never
        // the presence of a CostDoc. Those two usually AGREE (a completed run writes the doc and marks the
        // stage in one handler), so the review-note test above cannot tell them apart. They DIVERGE only here:
        // the stage says `done` yet no CostDoc is on file. A dispatcher that guarded on "does a CostDoc exist"
        // would find none and RE-RUN — re-pricing a stage the machine already marked complete, off a possibly
        // superseded set of codes. Only a status guard skips. This is the pin that makes swapping the guard for
        // `store.GetCostAsync(...) is not null` fail loudly.
        var (d, store, agents, catalog) = Sut();
        PricedCatalog(catalog);
        await store.UpsertProjectAsync(Project(costStatus: "done"));   // stage done, but NO CostDoc on file

        await d.OnRecordChangedAsync(Delivered(Dosing()), default);

        Assert.Null(await store.GetCostAsync(P));      // Cost did NOT run — the stage status alone stopped it
        Assert.Empty(catalog.Calls);
        Assert.Equal(0, agents.TotalCalls);
        Assert.Equal("done", CostStage(store).Status);
    }
}
