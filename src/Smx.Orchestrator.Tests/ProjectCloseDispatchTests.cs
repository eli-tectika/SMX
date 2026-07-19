using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// Project close, end to end through the bus. The approved VP GateDoc landing on the change feed IS the
/// close dispatch (record-as-bus, same as every other stage): Marker Library entries for the confirmed
/// codes (content-keyed, so the same code from any project maps to ONE doc), a Learned Conclusion, the
/// Decision stage → done, Procurement → released.
///
/// The dispatcher TRUSTS the gate record — POST /decision/determination (VpGate.Armable + the coverage
/// re-check + real-code confirmations) is the ONLY writer of an approved VP GateDoc.
public class ProjectCloseDispatchTests
{
    private const string P = "p1";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store,
                    InMemoryKnowledgeStore Knowledge, FakeLearnedConclusionsIndex Index)
        Sut(bool withKnowledge = true)
    {
        var store = new InMemoryRecordStore();
        var knowledge = new InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        var conclusions = new LearnedConclusionWriter(
            knowledge, index, new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance);
        var dispatcher = new StageDispatcher(store, new FakeAgentRuns(), conclusions, 2,
            withKnowledge ? knowledge : null);
        return (dispatcher, store, knowledge, index);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object round-tripped through the real
    /// router, never the instance the test is still holding.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    /// The one finalized code the fixtures share — RatioSignature derived, never hard-coded.
    private static MarkerCode Code() =>
        new("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "ratio 9:4");

    private static string Ratio => Code().RatioSignature;

    private static ProjectDoc Project(string pid, string decisionStatus = "awaiting-VP")
    {
        var p = ProjectDoc.Create(pid, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in Stages.All) p.Stages[s].Status = "done";
        p.Stages[Stages.Decision].Status = decisionStatus;
        return p;
    }

    private static GateDoc VpGateDoc(string pid, string status = "approved") => new()
    {
        Id = RecordIds.Gate(pid, GateTypes.Vp), ProjectId = pid, GateType = GateTypes.Vp,
        Status = status, ApprovedAt = status == "approved" ? "2026-07-16T12:00:00.0000000+00:00" : null,
        Reason = status == "locked" ? "the VP said no" : null,
    };

    /// Everything CloseProjectAsync resolves: the parked project, the constraints spec the ValidatedFor is
    /// copied from, the DosingDoc carrying the confirmed code, and the DecisionDoc the VP signed.
    private static async Task SeedClosableAsync(InMemoryRecordStore store, string pid)
    {
        await store.UpsertProjectAsync(Project(pid));
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(pid), ProjectId = pid,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand-protection")],
        });
        await store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(pid), ProjectId = pid, GeneratedAt = "t", Codes = [Code()],
        });
        await store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(pid), ProjectId = pid, GeneratedAt = "t",
            Components =
            [
                new("bottle",
                    Rows:
                    [
                        new DecisionRow("cas-zr", "Zr", Determinations.Recommended, 450.0,
                            new ClearedCriteria(true, true, true),
                            new TraceRefs(RecordIds.Verdict(pid, "cas-zr", "bottle"),
                                RecordIds.Dosing(pid), RecordIds.Cost(pid))),
                    ],
                    ProposedCode: new ProposedCode(Ratio, ["cas-zr", "cas-y"], "agent pick"),
                    ConfirmedCode: Ratio, ConfirmedBy: "VP R&D", ConfirmedReason: "reviewed on 16 Jul"),
            ],
        });
    }

    private static StageState DecisionStage(InMemoryRecordStore store, string pid) =>
        store.Documents.OfType<ProjectDoc>().Single(p => p.ProjectId == pid).Stages[Stages.Decision];

    /// The production sequence: POST /decision/determination WRITES the gate record, and the change feed
    /// then hands the dispatcher a snapshot of it. F3's re-read makes the ON-FILE record load-bearing —
    /// the close trusts the store, not the fed snapshot — so a fixture must never deliver a gate the
    /// store does not hold.
    private static async Task DeliverSignedGateAsync(StageDispatcher d, InMemoryRecordStore store, string pid)
    {
        await store.UpsertGateAsync(VpGateDoc(pid));
        await d.OnRecordChangedAsync(Delivered(VpGateDoc(pid)), default);
    }

    // ---- the close -------------------------------------------------------------------------------------

    [Fact]
    public async Task AVpApproval_WritesTheMarkerLibrary()
    {
        var (d, store, knowledge, _) = Sut();
        await SeedClosableAsync(store, P);

        await DeliverSignedGateAsync(d, store, P);

        var marker = Assert.Single(await knowledge.QueryMarkersAsync(null));
        // Composition comes from the DosingDoc code — markers, the ratio identity, and the anchor ppm.
        Assert.Equal(["cas-zr", "cas-y"], marker.Composition.Markers);
        Assert.Equal(Ratio, marker.Composition.Ratio);
        Assert.Equal(450.0, marker.Composition.Ppm);
        // ValidatedFor comes from the component's ConstraintsDoc spec — what this code is now known to work in.
        Assert.Equal("packaging", marker.ValidatedFor.Application);
        Assert.Equal("HDPE", marker.ValidatedFor.Material);
        Assert.Equal("brand-protection", marker.ValidatedFor.Objective);
        Assert.Equal(P, marker.SourceProject);
        Assert.Equal(MarkerStatus.Approved, marker.Status);
        Assert.Equal(0, marker.ReuseCount);

        var stage = DecisionStage(store, P);
        Assert.Equal("done", stage.Status);
        Assert.Null(stage.Error);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
    }

    [Fact]
    public async Task Redelivery_DoesNotDoubleWrite()
    {
        // The change feed is at-least-once. The knowledge writes are idempotent by CONTENT-KEYED id
        // (the same code upserts, never appends), and the awaiting-VP latch keeps the whole handler from
        // re-running at all — so the conclusion is written (and index-pushed) exactly once, not re-stamped
        // on every redelivery.
        var (d, store, knowledge, index) = Sut();
        await SeedClosableAsync(store, P);

        await DeliverSignedGateAsync(d, store, P);
        await d.OnRecordChangedAsync(Delivered(VpGateDoc(P)), default);   // redelivery

        var marker = Assert.Single(await knowledge.QueryMarkersAsync(null));
        Assert.Equal(0, marker.ReuseCount);            // a redelivery is not a reuse
        Assert.Equal([P], marker.LinkedProjects);
        Assert.Single(index.Pushed);                   // the close's conclusion was pushed once, not per delivery
        Assert.Equal("done", DecisionStage(store, P).Status);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
    }

    [Fact]
    public async Task AReusedCode_IncrementsReuseCount_OncePerProject()
    {
        // The SAME code (identical ratio + (cas, ppm) pairs) confirmed by a second project maps to the SAME
        // content-keyed doc: reuse increments ONCE per project (the pin is the projects-list on the doc,
        // not a counter bump per delivery), and the source history shows both projects.
        var (d, store, knowledge, _) = Sut();
        await SeedClosableAsync(store, "p1");
        await SeedClosableAsync(store, "p2");

        await DeliverSignedGateAsync(d, store, "p1");   // first close mints the entry
        await DeliverSignedGateAsync(d, store, "p2");   // second close is the reuse

        var marker = Assert.Single(await knowledge.QueryMarkersAsync(null));
        Assert.Equal(1, marker.ReuseCount);
        Assert.Equal("p1", marker.SourceProject);                 // the original provenance is never rewritten
        Assert.Equal(["p1", "p2"], marker.LinkedProjects);

        // Redelivering p2's gate must not double-count: p2 is already in the projects-list.
        // (p2's Decision stage is `done` now; the latch alone would absorb this — the projects-list is what
        // keeps the count honest even when the writes re-run.)
        await d.OnRecordChangedAsync(Delivered(VpGateDoc("p2")), default);
        Assert.Equal(1, (await knowledge.QueryMarkersAsync(null)).Single().ReuseCount);
    }

    [Fact]
    public async Task ACloseWritesALearnedConclusion()
    {
        var (d, store, knowledge, _) = Sut();
        await SeedClosableAsync(store, P);

        await DeliverSignedGateAsync(d, store, P);

        // Deterministic id — keyed by the project's close, so an at-least-once redelivery upserts one doc.
        var conclusion = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Decision, $"{P}|close");
        Assert.NotNull(conclusion);
        Assert.Equal(KnowledgeKinds.Decision, conclusion!.Kind);
        Assert.Contains(Ratio, conclusion.Finding);               // the finding names the confirmed ratio
        Assert.Contains(P, conclusion.Provenance.SourceProjects);
    }

    [Fact]
    public async Task AnUnapprovedVpGate_DoesNothing()
    {
        // A locked/rejected VP gate is the VP saying no — nothing may close off it. No knowledge writes,
        // stage untouched, procurement stays unreleased.
        var (d, store, knowledge, index) = Sut();
        await SeedClosableAsync(store, P);

        await d.OnRecordChangedAsync(Delivered(VpGateDoc(P, "locked")), default);

        Assert.Empty(await knowledge.QueryMarkersAsync(null));
        Assert.Empty(index.Pushed);
        Assert.Equal("awaiting-VP", DecisionStage(store, P).Status);
        Assert.Equal(ProcurementStatus.Unreleased, (await store.GetDecisionAsync(P))!.Procurement.Status);
    }

    [Fact]
    public async Task AClose_OverAnUnconfirmedDecision_ParksLoud_AndNeverReleases()
    {
        // The raced-close residue (Task 15 review F1, layer 1): a revision's persist can land a FRESH
        // DecisionDoc (Confirmed* null) between the VP's stamp and the gate's delivery. A close that
        // filtered to zero confirmations and carried on would release procurement over NOTHING — an empty
        // conclusion under a real signature, the worst artifact this system could mint. It must park LOUD
        // instead: needs-review, naming the unconfirmed components, nothing written, nothing released.
        var (d, store, knowledge, index) = Sut();
        await SeedClosableAsync(store, P);
        var decision = (await store.GetDecisionAsync(P))!;
        decision.Components = [.. decision.Components.Select(c => c with
        {
            ConfirmedCode = null, ConfirmedBy = null, ConfirmedReason = null,
        })];
        await store.UpsertDecisionAsync(decision);

        await DeliverSignedGateAsync(d, store, P);

        var stage = DecisionStage(store, P);
        Assert.Equal("needs-review", stage.Status);
        Assert.Contains("bottle", stage.Error);            // names the unconfirmed component
        Assert.Contains("re-sign", stage.Error);           // and the recovery: re-sign after the re-pick
        Assert.Equal(ProcurementStatus.Unreleased, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Empty(await knowledge.QueryMarkersAsync(null));
        Assert.Empty(index.Pushed);

        // A redelivered gate is absorbed — the stage is no longer awaiting-VP — so the park is stable.
        await DeliverSignedGateAsync(d, store, P);
        Assert.Equal("needs-review", DecisionStage(store, P).Status);
        Assert.Empty(await knowledge.QueryMarkersAsync(null));
    }

    [Fact]
    public async Task AnApproveRevokedBeforeTheFeedDelivered_DoesNotClose()
    {
        // F3: the feed hands SNAPSHOTS (the OnChatMessageAsync lesson). An approve stamped and revoked a
        // moment later delivers the approved snapshot while the STORE already holds `locked`. Closing off
        // the fed element would release procurement under a gate that is no longer signed — the handler
        // must re-read the record and trust only that.
        var (d, store, knowledge, index) = Sut();
        await SeedClosableAsync(store, P);
        await store.UpsertGateAsync(VpGateDoc(P, "locked"));   // the store's CURRENT truth: revoked

        await d.OnRecordChangedAsync(Delivered(VpGateDoc(P)), default);   // the stale approved snapshot

        Assert.Equal("awaiting-VP", DecisionStage(store, P).Status);      // no close: still parked
        Assert.Equal(ProcurementStatus.Unreleased, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Empty(await knowledge.QueryMarkersAsync(null));
        Assert.Empty(index.Pushed);
    }

    [Fact]
    public async Task AClose_ThatDiesMidWrite_StampsFailed_AndTheExceptionNeverEscapes()
    {
        // The close is the single highest-stakes transition and the only multi-step dispatch path doing
        // remote I/O (marker-library writes, the conclusion's embed + search push) — and ChangeFeedWorker
        // catches dispatch exceptions, LOGS, and CHECKPOINTS ANYWAY. An exception that escaped here is
        // checkpoint-and-lose: the project sits `awaiting-VP` forever under a signed gate, the dashboard
        // blaming a VP who already signed, and nothing ever redelivers. §11: nothing dies silently — the
        // close must stamp `failed` with the error, exactly like every stage runner.
        var (d, store, knowledge, index) = Sut();
        await SeedClosableAsync(store, P);
        knowledge.ThrowOnUpsertMarker = new InvalidOperationException("cosmos died mid-write — injected");

        await store.UpsertGateAsync(VpGateDoc(P));
        var escaped = await Record.ExceptionAsync(() => d.OnRecordChangedAsync(Delivered(VpGateDoc(P)), default));

        // The exception did NOT escape to the feed...
        Assert.Null(escaped);
        // ...the stage is stamped `failed` with the error visible...
        var stage = DecisionStage(store, P);
        Assert.Equal("failed", stage.Status);
        Assert.Contains("cosmos died mid-write — injected", stage.Error);
        // ...and nothing half-closed: procurement unreleased, no conclusion pushed.
        Assert.Equal(ProcurementStatus.Unreleased, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Empty(index.Pushed);

        // The `failed` stamp left `awaiting-VP`, so the latch absorbs redeliveries — the failure is
        // stable and visible, not a retry loop against a dead store.
        await d.OnRecordChangedAsync(Delivered(VpGateDoc(P)), default);
        Assert.Equal("failed", DecisionStage(store, P).Status);

        // Every write inside close is idempotent (content-keyed ids, the LinkedProjects pin, the
        // deterministic conclusion id), so once the operator clears the failure — re-arming the latch —
        // the SAME signed gate converges to a full close.
        knowledge.ThrowOnUpsertMarker = null;
        var project = (await store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "awaiting-VP";
        await store.UpsertProjectAsync(project);
        await d.OnRecordChangedAsync(Delivered(VpGateDoc(P)), default);

        Assert.Equal("done", DecisionStage(store, P).Status);
        Assert.Single(await knowledge.QueryMarkersAsync(null));
        Assert.Single(index.Pushed);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
    }

    [Fact]
    public async Task Close_WithNoKnowledgeStore_DegradesSafely()
    {
        // Mirror of the catalog-null degrade: no knowledge store ⇒ the knowledge writes are skipped, but
        // the project still closes — stage done, procurement released. The E2E covers the wired path.
        var (d, store, knowledge, index) = Sut(withKnowledge: false);
        await SeedClosableAsync(store, P);

        await DeliverSignedGateAsync(d, store, P);

        Assert.Equal("done", DecisionStage(store, P).Status);
        Assert.Equal(ProcurementStatus.Released, (await store.GetDecisionAsync(P))!.Procurement.Status);
        Assert.Empty(await knowledge.QueryMarkersAsync(null));    // nothing reached the (unwired) store
        Assert.Empty(index.Pushed);                               // and no conclusion was pushed
    }
}
