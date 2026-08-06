using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// Was RegulatoryGateEndpointsTests. The gate half of it is DELETED with the gate (§16.4) — fourteen tests
/// covering POST /regulatory/approve and GET /gate/regulatory, whose subjects no longer exist. Nothing was
/// deleted to make anything pass; those endpoints are not in the app.
///
/// WHAT WAS NOT DELETED, and why it matters more than before: POST /regulatory/review and POST
/// /regulatory/determination. They are the operator's remaining two acts on a verdict, and dropping the
/// gate raised the stakes on both. `review` writes the EvidenceReviewed flag that EvidenceReview.Outstanding
/// reads to refuse the VP signature and the order; `determination` writes the VETO that CompliantSet honours
/// against the agent's own proposal. Between them they are the whole of the human's authority over the
/// regulatory analysis now.
public class RegulatoryOperatorEntryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public RegulatoryOperatorEntryTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();

    private async Task SeedVerdict(string pid, string cas, VerdictStatus overall)
    {
        var proj = ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(pid, cas, "bottle"), ProjectId = pid, Cas = cas, ComponentId = "bottle",
            Element = "Zr", Form = "neodec",
            Dimensions = [new("ElementGate", overall, [new Citation("r", "x", "t")], 0.9, "r")],
        });
        // Register this (cas, bottle) cell as a non-C candidate so the verdict set is COMPLETE
        // (MatrixAssembler.IsComplete counts non-C cells and finds this verdict).
        var candidates = await _store.GetCandidatesAsync(pid)
            ?? new CandidatesDoc { Id = RecordIds.Candidates(pid), ProjectId = pid };
        candidates.Substances.Add(new CandidateSubstance(
            "bottle", "Zr", "neodec", cas, null, null, false, "A", "seed", []));
        await _store.UpsertCandidatesAsync(candidates);
    }

    [Fact]
    public async Task Review_MarksVerdictEvidenceReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "cas1", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.EvidenceReviewed);
    }

    [Fact]
    public async Task Review_Returns404_ForUnknownVerdict()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "nope", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Determination_Recommend_SetsFieldsAndReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Conditional);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = "supplier COA confirms compliance" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("recommended", v!.Determination);
        Assert.Equal("supplier COA confirms compliance", v.DeterminationReason);
        Assert.True(v.EvidenceReviewed);
    }

    [Fact]
    public async Task Determination_RecommendWithoutReason_Returns422()
    {
        // Every determination — including recommending a flagged item — must carry a reason.
        await SeedVerdict("p1", "cas1", VerdictStatus.Conditional);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.Determination);
    }

    [Fact]
    public async Task Determination_RejectWithoutReason_Returns422()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "rejected", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.Determination);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("approved")]      // a plausible synonym is still not the word
    [InlineData("Recommended")]   // CASE. CompliantSet's filter is ordinal, so a capital R would never be
    [InlineData(" recommended ")] // WHITESPACE. Same: an untrimmed string matches nothing downstream.
    public async Task Determination_ThatIsNotExactlyOneOfTheTwoConstants_Returns422(string determination)
    {
        // This endpoint is the ONLY writer of VerdictDoc.Determination, and CompliantSet reads that field
        // with an ordinal ==. So this 422 is what guarantees the string CompliantSet sees is always one of
        // the two constants. A future "helpful" OrdinalIgnoreCase or .Trim() here would let a determination
        // be persisted that the compliant-set filter cannot recognise — the operator signs, and the
        // substance silently never gets dosed. Fails closed, but it fails silently, which is worse to debug.
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination, reason = "a reason" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Determination_RejectWithReason_PersistsReasonAndReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "rejected", reason = "EU Cosmetics Annex III" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("rejected", v!.Determination);
        Assert.Equal("EU Cosmetics Annex III", v.DeterminationReason);
        Assert.True(v.EvidenceReviewed);
    }

    [Fact]
    public async Task Determination_Returns404_ForUnknownVerdict()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "nope", componentId = "bottle", determination = "recommended", reason = "n/a" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
