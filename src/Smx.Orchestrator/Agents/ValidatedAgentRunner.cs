using System.Text.Json;
using Smx.Domain;

namespace Smx.Orchestrator.Agents;

public sealed record AgentRunResult<T>(bool Succeeded, T? Output, string? Error)
{
    public static AgentRunResult<T> Ok(T output) => new(true, output, null);
    public static AgentRunResult<T> NeedsReview(string error) => new(false, default, error);
}

public static class ValidatedAgentRunner
{
    private const int MaxRetries = 2; // spec: 2 failed retries (3 attempts) → needs_review

    /// <param name="validate">returns null when valid, else a human-readable error fed back to the agent</param>
    public static async Task<AgentRunResult<T>> RunAsync<T>(
        ISmxAgent agent, string prompt, Func<T, string?> validate, CancellationToken ct)
    {
        var thread = await agent.StartThreadAsync(ct);
        var message = prompt;
        string lastError = "no attempts made";
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var text = await thread.SendAsync(message, ct);
            string? error;
            T? parsed = default;
            try
            {
                parsed = JsonSerializer.Deserialize<T>(StripFence(text), Json.Options);
                error = parsed is null ? "response deserialized to null" : validate(parsed);
            }
            catch (JsonException e)
            {
                error = $"response was not valid JSON matching the required schema: {e.Message}. " +
                        "Reply with ONLY the JSON object, no prose, no code fences.";
            }
            if (error is null) return AgentRunResult<T>.Ok(parsed!);
            lastError = error;
            message = $"Your previous response was rejected: {error}\n" +
                      "Correct the response. Reply with ONLY the corrected JSON object.";
        }
        return AgentRunResult<T>.NeedsReview(lastError);
    }

    internal static string StripFence(string text)
    {
        var t = text.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }
}
