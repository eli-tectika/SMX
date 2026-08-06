using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// THE IRREVERSIBLE ACT. The pipeline runs end to end without a human now (execution-core §8), so every
/// guard that used to stop the PIPELINE and protect procurement by side effect has to hold HERE instead.
///
/// Two of them moved into this file's subject when the parks were deleted:
///
///   - the regulatory coverage re-check, previously RunDosingAsync's `RegulatoryGate.Armable` early return.
///     A GateDoc has no binding to the verdicts it was signed over, so `approved` is not proof the CURRENT
///     analysis was reviewed.
///   - the provisional-dosing refusal, which is new: Dosing may now compute over the AGENT'S proposals and
///     an estimated floor, and the VP's signature does not retroactively re-run it.
///
/// If any test here starts passing its order, procurement has been opened over an analysis no human ruled.
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
        string? determination) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P, Cas = cas, ComponentId = "bottle",
        Element = element, Form = "f",
        Dimensions = [new("ElementGate", status, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = reviewed,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
    };

    /// A fully CLOSED project: VP signed, procurement released, one confirmed code, a sheet on file. Every
    /// test below breaks exactly one thing about it, so a refusal can only be the thing it broke.
    private async Task SeedOrderableAsync(bool provisional = false, VerdictDoc? lateVerdict = null)
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
        await _store.UpsertVerdictAsync(
            Verdict(OrderableCas, "Zr", VerdictStatus.Pass, true, Determinations.Recommended));
        await _store.UpsertVerdictAsync(
            lateVerdict ?? Verdict("cas-y", "Y", VerdictStatus.Pass, true, Determinations.Recommended));

        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-08-01T00:00:00.0000000+00:00", ApprovedBy = "operator",
        });

        var code = Code();
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t", Codes = [code],
            Provisional = provisional,
            ProvisionalReasons = provisional
                ? ["Zr (cas-zr) in 'bottle' is included on the agent's proposal alone — no operator determination is on file."]
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
    public async Task AProvisionalDosing_RefusesTheOrder_AndNamesWhy()
    {
        // The dosing was computed over the agent's proposals. The VP's signature does NOT retroactively
        // re-run Dosing, so this state is reachable on a legitimately released project -- and ordering
        // would buy a chemical at a dose nobody ruled on.
        await SeedOrderableAsync(provisional: true);

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("provisional", body);
        Assert.Contains("proposal alone", body);   // the REASON travels, not just the refusal
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task ALateUnreviewedFailingVerdict_RefusesTheOrder_EvenUnderASignedGate()
    {
        // The check that used to live in RunDosingAsync and was deleted with the parks. A fresh, unreviewed,
        // FAILING verdict is live under the existing signature -- the POST /approve vs. late-verdict race.
        // The gate still reads `approved`; the analysis it covers has changed underneath it.
        await SeedOrderableAsync(
            lateVerdict: Verdict("cas-y", "Y", VerdictStatus.Fail, reviewed: false, determination: null));

        var res = await OrderAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("no longer covers", body);
        Assert.Contains("cas-y", body);            // names the verdict that broke the coverage
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
