using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    [Fact]
    public void ScreeningTools_ExposeFourNamedFunctions()
    {
        var box = new ToolBox(new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(), new FakeSearch());
        var names = box.ScreeningTools().Select(t => t.Name).ToList();
        Assert.Equal(["lookup_compatibility", "search_regulatory", "search_sds", "search_reference"], names);
    }

    [Fact]
    public void IntakeTools_ExposeRegulatoryAndReferenceOnly()
    {
        var box = new ToolBox(new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(), new FakeSearch());
        Assert.Equal(["search_regulatory", "search_reference"], box.IntakeTools().Select(t => t.Name).ToList());
    }

    [Fact]
    public async Task LookupCompatibility_DelegatesToLookup_AndReportsUntabulated()
    {
        var lookup = new FakeCompatibilityLookup();
        var box = new ToolBox(lookup, new FakeSearch(), new FakeSearch(), new FakeSearch());
        var result = await box.LookupCompatibilityAsync("Zr", "HDPE", default);
        Assert.Contains("not tabulated", result);
        Assert.Single(lookup.Calls);
    }
}
