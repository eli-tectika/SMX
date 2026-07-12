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
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("recommended", v!.Determination);
        Assert.True(v.EvidenceReviewed);
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
}
