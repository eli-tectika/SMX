using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RegulatoryGateEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public RegulatoryGateEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

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

    [Fact]
    public async Task Determination_UnknownValue_Returns422()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "maybe", reason = (string?)null });
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

    [Fact]
    public async Task Approve_Returns422WithBlockers_WhenFlaggedItemUnreviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail); // flagged + unreviewed
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("cas1", await resp.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task Approve_WritesApprovedGate_WhenArmable()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass); // clean → armable without review
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var g = await _store.GetGateAsync("p1", GateTypes.Regulatory);
        Assert.NotNull(g);
        Assert.Equal("approved", g!.Status);
        Assert.False(string.IsNullOrEmpty(g.ApprovedAt));
    }

    [Fact]
    public async Task Approve_Returns422_WhenVerdictSetIncomplete()
    {
        // A non-C candidate with NO verdict → MatrixAssembler.IsComplete is false.
        var proj = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = { new CandidateSubstance("bottle", "Zr", "neodec", "cas1", null, null, false, "A", "seed", []) },
        });
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("incomplete", await resp.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task GetGate_ReportsLockedWithBlockers_BeforeApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("locked", g.GetProperty("status").GetString());
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("cas1", g.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task GetGate_ReportsApproved_AfterApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("approved", g.GetProperty("status").GetString());
        Assert.True(g.GetProperty("armable").GetBoolean());
    }

    [Fact]
    public async Task Approve_Twice_PreservesOriginalApprovedAt()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var first = (await _store.GetGateAsync("p1", GateTypes.Regulatory))!.ApprovedAt;
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var second = (await _store.GetGateAsync("p1", GateTypes.Regulatory))!.ApprovedAt;
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetGate_ReportsNotArmableWithIncompleteBlocker_WhenVerdictSetIncomplete()
    {
        // A candidate with no verdict → incomplete set. Build candidates directly (SeedVerdict would add a verdict).
        var proj = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new CandidateSubstance("bottle", "Zr", "neodec", "cas1", null, null, false, "A", "seed", [])],
        });
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("incomplete", g.GetProperty("blockers").ToString());
    }
}
