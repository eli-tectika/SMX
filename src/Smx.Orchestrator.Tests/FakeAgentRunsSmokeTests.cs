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

    // Task 8's chat-dispatch tests assert on both of these, so pin them here: the default reply ECHOES the
    // operator's message (a dispatch test can therefore prove the right message reached the agent, not just
    // that some string came back), and ChatCalls counts deliveries — which is what an at-least-once change
    // feed's idempotency guard has to be measured against.
    [Fact]
    public async Task Fake_DefaultChat_EchoesTheMessageAndCountsTheCall()
    {
        var fake = new FakeAgentRuns();
        var reply = await ((Smx.Orchestrator.Dispatch.IAgentRuns)fake).RunChatAsync(
            Stages.Discovery,
            new Smx.Orchestrator.Agents.ChatTools(new Smx.Domain.Tests.Fakes.InMemoryRecordStore(), "p1", Stages.Discovery, "k1"),
            thread: "(no prior conversation)", stageInputsJson: "{}", message: "why Ba?", ct: default);

        Assert.Equal("Echo: why Ba?", reply);
        Assert.Equal(1, fake.ChatCalls);
    }
}
