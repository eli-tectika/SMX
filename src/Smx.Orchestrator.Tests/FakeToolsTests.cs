using Smx.Domain.Tools;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class FakeToolsTests
{
    [Fact]
    public async Task FakeLearnedConclusionsSearch_RecordsQuery_AndHonorsTop()
    {
        var fake = new FakeLearnedConclusionsSearch
        {
            Results = { new RetrievedChunk("learned-conclusions", "learned-conclusions/1", "a", 0.9),
                        new RetrievedChunk("learned-conclusions", "learned-conclusions/2", "b", 0.8) },
        };
        var got = await fake.SearchAsync("zr bottle", top: 1);
        Assert.Single(got);
        Assert.Equal("zr bottle", fake.Queries.Single());
    }
}
