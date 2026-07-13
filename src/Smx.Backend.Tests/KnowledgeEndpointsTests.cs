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
}
