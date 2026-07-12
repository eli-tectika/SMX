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
}
