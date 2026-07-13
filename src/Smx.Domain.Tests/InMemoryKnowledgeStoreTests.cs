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
    public async Task QueryLearnedConclusions_SearchesScopeForm()
    {
        // Form is a first-class scope dimension for Discovery tiering; a browse that omits it hides
        // every form-scoped conclusion from the operator.
        var store = new InMemoryKnowledgeStore();
        var c = Conclusion("zr|bottle", "Prefer this form.");
        await store.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = c.Id, Kind = c.Kind, Finding = c.Finding, Confidence = c.Confidence,
            Scope = new ConclusionScope("Zr", "neodecanoate", "bottle", null, null, null),
            Provenance = c.Provenance, CreatedAt = c.CreatedAt,
        });
        Assert.Single(await store.QueryLearnedConclusionsAsync("neodecanoate"));
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
    public async Task FindMarkers_AndsSuppliedDimensions_IgnoresNulls_AndOnlyApproved()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMarkerAsync(Marker("m1", "anti-counterfeit"));                       // label/overt
        await store.UpsertMarkerAsync(new MarkerLibraryDoc                                     // same app, different material
        {
            Id = KnowledgeIds.Marker("m2"), Composition = new MarkerComposition(["Y"], 100, "1:0"),
            ValidatedFor = new ValidatedFor("anti-counterfeit", "bottle", "covert"), SourceProject = "p2", CreatedAt = "t",
        });
        await store.UpsertMarkerAsync(new MarkerLibraryDoc                                     // matches on every dimension but retired
        {
            Id = KnowledgeIds.Marker("m3"), Composition = new MarkerComposition(["Zr"], 200, "1:0"),
            ValidatedFor = new ValidatedFor("anti-counterfeit", "label", "overt"), SourceProject = "p3",
            Status = "superseded", CreatedAt = "t",
        });

        // AND over the supplied dimensions: m2's material excludes it, m3 is not approved for reuse.
        var hit = await store.FindMarkersAsync("anti-counterfeit", "label", "overt");
        Assert.Equal([KnowledgeIds.Marker("m1")], hit.Select(m => m.Id));

        // A null dimension is not constrained (m3 still excluded — status is not a dimension).
        Assert.Equal(2, (await store.FindMarkersAsync("anti-counterfeit", null, null)).Count);
        Assert.Equal(2, (await store.FindMarkersAsync(null, null, null)).Count);

        // Case-insensitive contains per dimension.
        Assert.Single(await store.FindMarkersAsync("ANTI-COUNTERFEIT", "LAB", null));

        // A dimension that matches nothing excludes everything, even though the others match.
        Assert.Empty(await store.FindMarkersAsync("anti-counterfeit", "label", "banknote"));
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
