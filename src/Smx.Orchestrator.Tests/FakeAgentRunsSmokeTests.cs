using Smx.Domain.Records;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class FakeAgentRunsSmokeTests
{
    [Fact]
    public async Task Fake_DefaultDiscovery_ReturnsOneCandidate()
    {
        var fake = new FakeAgentRuns();
        var c = new ConstraintsDoc { Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)] };
        var result = await ((Smx.Orchestrator.Dispatch.IAgentRuns)fake).RunDiscoveryAsync(c, null, default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.Substances);
    }
}
