using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// THE IRREVERSIBLE ACT, and after 16.4 it carries almost all of the weight. The pipeline runs end to end
/// without a human (execution-core 8) and the regulatory gate is gone entirely, so every guard that used to
/// stop the PIPELINE - or to sit on a signature that no longer exists - has to hold HERE instead:
///
///   - the flagged-findings check (EvidenceReview.Outstanding, previously RegulatoryGate.Armable, and
///     previously still RunDosingAsync's early return). A live non-Pass verdict nobody has opened refuses
///     the order. It never needed a signature to mean something, which is why it outlived one.
///   - the provisional-dosing refusal: a ppm sitting above a floor nobody measured.
///   - MSDS-before-order, and the VP's confirmed code.
///
/// THE OPPOSITE FAILURE MATTERS JUST AS MUCH HERE, and it is why AProjectNobodyRuledOn_CanStillOrder
/// exists. Dropping the regulatory gate removed the only writer of operator determinations; had
/// CompliantSet and the provisional flag not been rewritten with it, every order would be refused forever
/// on an app that looked entirely healthy. A test suite that only ever checks refusals cannot see that.
///
/// If any test here starts passing its order, procurement has been opened over an analysis nobody checked.
public class OrderPreconditionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-order-1";
    private const string OrderableCas = "cas-zr";

    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly InMemorySdsCorpusReader _corpus = new();
    private readonly HttpClient _client;

    public OrderPreconditionTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
            s.AddSingleton<ISdsCorpusReader>(_corpus);
        })).CreateClient();

    private static MarkerCode Code() => new("bottle",
        [new CodeMarker(OrderableCas, "Zr", 450.0, 0.74, 1.0, 1.35),
         new CodeMarker("cas-y", "Y", 200.0, 0.70, 0.5, 0.9)],
        "the confirmed pair");

    private static VerdictDoc Verdict(string cas, string element, VerdictStatus status, bool reviewed,
        string? determination, string? proposed = null) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P, Cas = cas, ComponentId = "bottle",
        Element = element, Form = "f",
        Dimensions = [new("ElementGate", status, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = reviewed,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
        ProposedDetermination = proposed,
        ProposedReason = proposed is null ? null : "the agent's proposal",
    };

    /// A fully CLOSED project: VP signed, procurement released, one confirmed code, a sheet on file. Every
    /// test below breaks exactly one thing about it, so a refusal can only be the thing it broke.
    ///
    /// `ruled` is the switch that carries this file's newest test. When false the verdicts carry ONLY the
    /// agent's proposal - which is the state of every project on an unattended run now that nothing writes
    /// operator determinations - and the order must still go through.
    private async Task SeedOrderableAsync(
        bool provisional = false, VerdictDoc? lateVerdict = null, bool ruled = true)
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in Stages.All) p.Stages[s].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(p);

        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances =
            [
                new("bottle", "Zr", "f", OrderableCas, null, null, false, "A", "s", []),
                new("bottle", "Y", "f", "cas-y", null, null, false, "A", "s", []),
            ],
        });
        // ruled: the operator's own `recommended`. unruled: the agent's proposal and nothing else.
        var determination = ruled ? Determinations.Recommended : null;
        var proposed = ruled ? null : Determinations.Recommended;
        await _store.UpsertVerdictAsync(
            Verdict(OrderableCas, "Zr", VerdictStatus.Pass, true, determination, proposed));
        await _store.UpsertVerdictAsync(
            lateVerdict ?? Verdict("cas-y", "Y", VerdictStatus.Pass, true, determination, proposed));

        var code = Code();
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t", Codes = [code],
            Provisional = provisional,
            // The ONE thing `provisional` still means (16.4): a ppm above a number nobody measured.
            ProvisionalReasons = provisional
                ? ["Zr in 'bottle': device limit of detection - no physicist measurement on file."]
                : [],
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [],
                new ProposedCode(code.RatioSignature, [OrderableCas, "cas-y"], "proposed"),
                ConfirmedCode: code.RatioSignature, ConfirmedBy: "VP R&D", ConfirmedReason: "reviewed")],
            Procurement = new ProcurementState { Status = ProcurementStatus.Released },
        });

        _corpus.Sheets.Add(new SdsCorpusSheet(
            OrderableCas, "Acme Chemicals", "product", "2026-05-01", "2026-05-02T00:00:00Z"));
    }

    private Task<HttpResponseMessage> OrderAsync() =>
        _client.PostAsync($"/projects/{P}/orders/{OrderableCas}", null);

    [Fact]
    public async Task AFullySignedProject_CanOrder()
    {
        // The control. Without it, every refusal below could be passing for the wrong reason.
        await SeedOrderableAsync();

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Contains(OrderableCas, (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task AProjectNobodyRuledOn_CanStillOrder()
    {
        // THE TEST THIS WHOLE CHANGE EXISTS TO KEEP GREEN, and the one a suite of refusals would never
        // have caught. Not one verdict here carries an operator determination - only the agent's proposal.
        // That is now the state of EVERY project after an unattended run, because deleting the regulatory
        // gate deleted the only thing that wrote determinations.
        //
        // Had CompliantSet stayed strict, this order would be refused forever: an empty compliant set, a
        // dosing flagged provisional for resting on a proposal, and POST /orders 422ing on every project
        // this system will ever run - with no error, no failed stage, and nothing on any screen to say so.
        // An app that quietly never lets anyone buy anything is worse than one that visibly breaks.
        await SeedOrderableAsync(ruled: false);

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Contains(OrderableCas, (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task AnOperatorVetoRecordedAfterDosing_RefusesTheOrder()
    {
        // THE OTHER SIDE OF THE LOOSENED CompliantSet. Admitting the agent's proposal by default is only
        // safe because the operator's `rejected` is absolute, so this is the test that keeps it absolute
        // at the one place it matters.
        //
        // The hole it closes is specific: the VP-confirmed code was composed from a compliant set that
        // included cas-zr, and the operator vetoed it AFTERWARDS. Nothing else on this endpoint sees that
        // - the verdict is a `Pass` so it is not a flagged finding, and recording a determination sets
        // EvidenceReviewed so it is not an unopened one. Without the veto re-read, procurement would buy a
        // chemical a human explicitly refused, on a project that looks perfectly closed.
        await SeedOrderableAsync();
        var veto = Verdict(OrderableCas, "Zr", VerdictStatus.Pass, reviewed: true,
            determination: Determinations.Rejected, proposed: Determinations.Recommended);
        await _store.UpsertVerdictAsync(veto);

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("rejected", body);
        Assert.Contains("bottle", body);        // names the component whose ruling refuses it
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task AProvisionalDosing_RefusesTheOrder_AndNamesWhy()
    {
        // The dosing rests on a floor nobody measured. The VP's signature does NOT retroactively re-run
        // Dosing, so this state is reachable on a legitimately released project -- and ordering would buy
        // a marker that may be undetectable in the field.
        await SeedOrderableAsync(provisional: true);

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("provisional", body);
        Assert.Contains("no physicist measurement", body);   // the REASON travels, not just the refusal
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task ALateUnreviewedFailingVerdict_RefusesTheOrder_EvenUnderASignedVpGate()
    {
        // The check that used to live in RunDosingAsync, then on the regulatory gate, and now lives here -
        // and this is the test that proves it can still FIRE. A fresh, unreviewed, FAILING verdict is live
        // under an existing VP signature (a revise's leftovers, a race with a late Regulatory child).
        //
        // Nothing machine-side can clear EvidenceReviewed any more (REGULATORY_AUTO_APPROVE went with the
        // gate), so the only way past this refusal is the operator opening the item. That is the point: a
        // check nobody can trip is worse than no check, because it reads as protection.
        await SeedOrderableAsync(
            lateVerdict: Verdict("cas-y", "Y", VerdictStatus.Fail, reviewed: false, determination: null));

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("have not been opened", body);
        Assert.Contains("cas-y", body);            // names the verdict that is unopened
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task NoCandidates_RefusesTheOrder_RatherThanReadingAnEmptyAnalysisAsAClearOne()
    {
        // The absence path, which must lean the same way as the failure path: no candidates means every
        // verdict is an orphan and there IS no analysis under the signature. Read as "nothing failed", it
        // would be the most permissive state in the system.
        await SeedOrderableAsync();
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P, Substances = [],
        });

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }
}
