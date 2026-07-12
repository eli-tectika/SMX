using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class InMemoryKnowledgeStoreTests
{
    private static LearnedConclusionDoc Conclusion(string scopeKey, string finding) => new()
    {
        Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Material, scopeKey), Kind = KnowledgeKinds.Material,
        Scope = new ConclusionScope("Zr", null, "bottle", null, null, null), Finding = finding,
        Confidence = 0.9, Provenance = new ConclusionProvenance(["p1"], []), CreatedAt = "t",
    };

    private static MarkerLibraryDoc Marker(string key, string application) => new()
    {
        Id = KnowledgeIds.Marker(key), Composition = new MarkerComposition(["Zr"], 200, "1:0"),
        ValidatedFor = new ValidatedFor(application, "label", "overt"), SourceProject = "p1", CreatedAt = "t",
    };

    [Fact]
    public async Task LearnedConclusion_RoundTrips_AndQueryMatchesFinding()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertLearnedConclusionAsync(Conclusion("zr|bottle", "Zr neodecanoate is the preferred bottle form."));
        Assert.Equal("Zr neodecanoate is the preferred bottle form.",
            (await store.GetLearnedConclusionAsync(KnowledgeKinds.Material, "zr|bottle"))!.Finding);
        Assert.Single(await store.QueryLearnedConclusionsAsync("neodecanoate"));
        Assert.Empty(await store.QueryLearnedConclusionsAsync("cadmium"));
    }

    [Fact]
    public async Task Marker_RoundTrips_AndQueryMatchesValidatedFor()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMarkerAsync(Marker("m1", "anti-counterfeit"));
        Assert.Equal("anti-counterfeit", (await store.GetMarkerAsync(KnowledgeIds.Marker("m1")))!.ValidatedFor.Application);
        Assert.Single(await store.QueryMarkersAsync("anti-counterfeit"));
        Assert.Empty(await store.QueryMarkersAsync("banknote"));
    }

    [Fact]
    public async Task Msds_RoundTrips_ByCas()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMsdsAsync(new MsdsRegistryDoc { Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d" });
        Assert.Equal("Acme", (await store.GetMsdsAsync("c1"))!.Supplier);
        Assert.Single(await store.QueryMsdsAsync(null));
        Assert.Null(await store.GetMsdsAsync("nope"));
    }
}
