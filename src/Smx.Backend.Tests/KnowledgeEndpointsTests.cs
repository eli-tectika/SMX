using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class KnowledgeEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly HttpClient _client;

    public KnowledgeEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IKnowledgeStore>(_knowledge))).CreateClient();
    }

    [Fact]
    public async Task GetMarkerLibrary_ReturnsMatches_AndEmptyArrayOnColdStart()
    {
        var empty = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anything");
        Assert.Equal(0, empty.GetArrayLength());

        await _knowledge.UpsertMarkerAsync(new MarkerLibraryDoc
        {
            Id = KnowledgeIds.Marker("m1"), Composition = new(["Zr"], 200, "1:0"),
            ValidatedFor = new("anti-counterfeit", "label", "overt"), SourceProject = "p1", CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anti-counterfeit");
        Assert.Equal(1, hit.GetArrayLength());
    }

    [Fact]
    public async Task GetLearnedConclusions_FiltersBySearch()
    {
        await _knowledge.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Material, "zr|bottle"), Kind = KnowledgeKinds.Material,
            Scope = new("Zr", null, "bottle", null, null, null), Finding = "Zr neodecanoate preferred.",
            Confidence = 0.9, Provenance = new(["p1"], []), CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=neodecanoate");
        Assert.Equal(1, hit.GetArrayLength());
        var miss = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=cadmium");
        Assert.Equal(0, miss.GetArrayLength());
    }

    [Fact]
    public async Task Msds_Review_FlipsStatus_And404ForUnknown()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7", Supplier = "Acme", Version = "3", Date = "2025-01-01",
        });
        var ok = await _client.PostAsJsonAsync("/msds-registry/13463-67-7/review", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var reviewed = (await _knowledge.GetMsdsAsync("13463-67-7"))!;
        Assert.Equal(MsdsReviewStatus.Reviewed, reviewed.ReviewStatus);
        // A gate record must carry when it was signed, not just that it was.
        Assert.NotNull(reviewed.ReviewedAt);
        Assert.InRange(DateTimeOffset.Parse(reviewed.ReviewedAt!), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

        var missing = await _client.PostAsJsonAsync("/msds-registry/nope/review", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task GetMsds_BrowsesAll()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d",
        });
        var all = await _client.GetFromJsonAsync<JsonElement>("/msds-registry");
        Assert.Equal(1, all.GetArrayLength());
    }
}
