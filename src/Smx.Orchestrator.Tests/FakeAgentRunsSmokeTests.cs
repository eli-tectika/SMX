using Smx.Domain.Records;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class FakeAgentRunsSmokeTests
{
    [Fact]
    public async Task Fake_DefaultDiscovery_ReturnsOneCandidate()
    {
        var fake = new FakeAgentRuns();
        var p = ProjectDoc.Create("p1", "Acme", "Bottle", System.Text.Json.JsonDocument.Parse("{}").RootElement);
        var c = new ConstraintsDoc { Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)] };
        var result = await ((Smx.Orchestrator.Dispatch.IAgentRuns)fake).RunDiscoveryAsync(p, c, null, default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.Substances);
    }

    // The decision-dispatch tests assert on all of these, so pin them here: the default run mirrors the
    // assembled matrix and proposes the FIRST finalized code per component (never a confirmation — that is
    // the VP's field), DecisionCalls counts deliveries, and TotalCalls INCLUDES DecisionCalls. The last pin
    // is the load-bearing one: the Cost-is-agent-free test asserts TotalCalls == 0, and a decision counter
    // missing from that sum would let a decision agent call slip into Cost dispatch unseen.
    [Fact]
    public async Task Fake_DefaultDecision_MirrorsTheAssemblyProposesTheFirstCode_AndCountsTheCall()
    {
        var fake = new FakeAgentRuns();
        var dosing = new DosingDoc
        {
            Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "t",
            Codes = [new MarkerCode("bottle",
                [new CodeMarker("cas-zr", "Zr", 100, 0.74, 1, 2), new CodeMarker("cas-y", "Y", 44, 0.7, 1, 2)], "r")],
        };
        var assembled = new List<ComponentDecision>
        {
            new("bottle", Rows: [new DecisionRow("cas-zr", "Zr", Determinations.Recommended, 100,
                new ClearedCriteria(true, true, true), new TraceRefs("v", "w", "a"))], ProposedCode: null),
        };

        var result = await ((Smx.Orchestrator.Dispatch.IAgentRuns)fake).RunDecisionAsync(assembled, dosing, null, default);

        Assert.True(result.Succeeded);
        Assert.Equal(RecordIds.Decision("p1"), result.Output!.Id);
        var bottle = Assert.Single(result.Output.Components);
        Assert.Equal("cas-zr", Assert.Single(bottle.Rows).Cas);                 // the assembly, mirrored
        Assert.Equal("Zr:Y = 1.00:0.44", bottle.ProposedCode!.RatioSignature);  // the first code, proposed
        Assert.Null(bottle.ConfirmedCode);                                      // and NEVER confirmed
        Assert.Equal(1, fake.DecisionCalls);
        Assert.Equal(1, fake.TotalCalls);
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
            new Smx.Orchestrator.Agents.ChatTools(new Smx.Domain.Tests.Fakes.InMemoryRecordStore(), "p1", Stages.Discovery, "k1"),
            thread: "(no prior conversation)", stageInputsJson: "{}", message: "why Ba?", ct: default);

        Assert.Equal("Echo: why Ba?", reply);
        Assert.Equal(1, fake.ChatCalls);
    }
}
