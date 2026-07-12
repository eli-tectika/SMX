using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    private static ToolBox Box(
        Smx.Domain.IKnowledgeStore? knowledge = null,
        Smx.Domain.Tools.ILearnedConclusionsSearch? learnedConclusions = null)
    {
        var search = new FakeSearch();
        return new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            knowledge ?? new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(),
            learnedConclusions ?? new FakeLearnedConclusionsSearch());
    }

    [Fact]
    public void DiscoveryTools_ExposeCatalogCompatibilityReference()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["lookup_compatibility", "search_catalog", "search_learned_conclusions", "search_reference"], names);
    }

    [Fact]
    public void RegulatoryTools_ExposeRegulatorySdsReference_NoCompatibility()
    {
        var names = Box().RegulatoryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["search_reference", "search_regulatory", "search_sds"], names);
        Assert.DoesNotContain("lookup_compatibility", names);
    }

    [Fact]
    public async Task SearchCatalog_RendersEmptyAsNoMatchNote()
    {
        var json = await Box().SearchCatalogAsync("Xx", default);
        Assert.Contains("no matches", json);
    }

    [Fact]
    public void IntakeTools_IncludeMarkerLibraryAndLearnedConclusions()
    {
        var names = Box().IntakeTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Contains("search_marker_library", names);
        Assert.Contains("search_learned_conclusions", names);
    }

    [Fact]
    public void DiscoveryTools_IncludeLearnedConclusions()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).ToArray();
        Assert.Contains("search_learned_conclusions", names);
    }

    [Fact]
    public async Task SearchMarkerLibrary_EmptyStore_ReturnsNoMatchesSentinel()
    {
        var json = await Box().SearchMarkerLibraryAsync("anti-counterfeit", default);
        Assert.Contains("no matches", json);
    }

    [Fact]
    public async Task SearchLearnedConclusions_EmptyIndex_ReturnsNoMatchesSentinel()
    {
        var json = await Box().SearchLearnedConclusionsAsync("zr bottle", default);
        Assert.Contains("no matches", json);
    }

    [Fact]
    public async Task SearchMarkerLibrary_ReturnsSeededMatch()
    {
        var knowledge = new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore();
        await knowledge.UpsertMarkerAsync(new Smx.Domain.Records.MarkerLibraryDoc
        {
            Id = Smx.Domain.Records.KnowledgeIds.Marker("m1"),
            Composition = new(["Zr"], 200, "1:0"), ValidatedFor = new("anti-counterfeit", "label", "overt"),
            SourceProject = "p1", CreatedAt = "t",
        });
        var json = await Box(knowledge: knowledge).SearchMarkerLibraryAsync("anti-counterfeit", default);
        Assert.Contains("anti-counterfeit", json);
        Assert.DoesNotContain("no matches", json);
    }
}
