using Smx.Backend.Agents;
using Smx.Backend.Tests.Fakes;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

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

    /// The two rejected attempts vanish today. They are the system working — the validator caught a
    /// bad output and made the agent fix it — and an operator who cannot see them cannot tell a
    /// struggling run from a fast one.
    [Fact]
    public async Task Each_rejected_attempt_writes_a_step()
    {
        var trail = new RecordingTrail();
        var agent = new ScriptedAgent("""{"value":"x"}""", """{"value":"y"}""", """{"value":"abc"}""")
        {
            Trail = trail,
        };

        await ValidatedAgentRunner.RunAsync<Out>(agent, "go", RequireAbc, default);

        var rejected = trail.Steps.Where(s => s.Kind == RunStepKind.Rejected).ToList();
        Assert.Equal(2, rejected.Count);
        Assert.Contains("attempt 2 of 3", rejected[0].Text);
        Assert.Equal(2, rejected[0].Detail?.Attempt);
        Assert.Equal(3, rejected[0].Detail?.Of);
        Assert.Contains("attempt 3 of 3", rejected[1].Text);
    }

    /// The LAST attempt's failure is the run's OUTCOME, not a retry — writing "retrying, attempt 4 of 3"
    /// would promise the operator a turn that never happens.
    [Fact]
    public async Task The_final_failure_is_not_written_as_a_retry()
    {
        var trail = new RecordingTrail();
        var agent = new ScriptedAgent("""{"value":"x"}""", """{"value":"y"}""", """{"value":"z"}""")
        {
            Trail = trail,
        };

        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "go", RequireAbc, default);

        Assert.False(result.Succeeded);
        Assert.Equal(2, trail.Steps.Count(s => s.Kind == RunStepKind.Rejected));
    }
}
