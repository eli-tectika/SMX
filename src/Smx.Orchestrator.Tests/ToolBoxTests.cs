using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    private static ToolBox Box()
    {
        var search = new FakeSearch();
        return new ToolBox(new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search);
    }

    [Fact]
    public void DiscoveryTools_ExposeCatalogCompatibilityReference()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["lookup_compatibility", "search_catalog", "search_reference"], names);
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
}
