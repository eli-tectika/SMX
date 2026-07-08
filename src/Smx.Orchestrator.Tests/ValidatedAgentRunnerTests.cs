using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ValidatedAgentRunnerTests
{
    private sealed record Out(string Value);
    private static string? RequireAbc(Out o) => o.Value == "abc" ? null : $"value must be 'abc', got '{o.Value}'";

    [Fact]
    public async Task ValidOnFirstTry_ReturnsParsedOutput()
    {
        var agent = new ScriptedAgent("""{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Equal("abc", result.Output!.Value);
        Assert.Single(agent.Received);
    }

    [Fact]
    public async Task InvalidThenValid_FeedsValidationErrorBack_SameThread()
    {
        var agent = new ScriptedAgent("""{"value":"xyz"}""", """{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Equal(2, agent.Received.Count);
        Assert.Contains("value must be 'abc'", agent.Received[1]); // feedback carried the validator message
    }

    [Fact]
    public async Task UnparseableJson_GetsParseFeedback()
    {
        var agent = new ScriptedAgent("not json at all", """{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Contains("valid JSON", agent.Received[1]);
    }

    [Fact]
    public async Task ThreeInvalidAttempts_ReturnsNeedsReview_WithLastError()
    {
        var agent = new ScriptedAgent("""{"value":"x"}""", """{"value":"y"}""", """{"value":"z"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.False(result.Succeeded);
        Assert.Contains("value must be 'abc'", result.Error);
        Assert.Equal(3, agent.Received.Count); // initial + 2 retries, then give up
    }

    [Fact]
    public async Task JsonFence_IsStripped()
    {
        var agent = new ScriptedAgent("Here you go:\n```json\n{\"value\":\"abc\"}\n```");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
    }
}
