using System.Text.Json.Serialization;

namespace Smx.Domain.Records;

/// The terminal states a run can reach. `Interrupted` is not a failure the agent caused — it means the
/// process holding the run died, and it exists so the trail shows the gap rather than hiding it.
public static class RunOutcome
{
    public const string Running = "running";
    public const string Done = "done";
    public const string NeedsReview = "needs-review";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public static class RunStepKind
{
    public const string Started = "started";
    public const string ToolCall = "tool-call";
    public const string Rejected = "rejected";
    public const string Output = "output";
    public const string Outcome = "outcome";
}

public static class RunTriggers
{
    public const string Pipeline = "pipeline";
    public const string OperatorRetry = "operator-retry";
    public const string Revision = "revision";
    public const string Restart = "restart";
}

public static class RunIds
{
    /// '|' separated for the same reason every other record id is: it is id-safe in Cosmos and in a URL
    /// path segment once encoded, and it never occurs in a stage name or a project id.
    public static string Run(string projectId, string stage, int ordinal) =>
        $"run|{projectId}|{stage}|{ordinal}";
}

public sealed class RunStepDetail
{
    public string? Tool { get; set; }
    public string? Query { get; set; }
    public int? ResultCount { get; set; }
    /// The record this step WROTE — the audit link from a sentence to the change it made.
    public string? RecordId { get; set; }
    public int? Attempt { get; set; }
    public int? Of { get; set; }
}

public sealed class RunStep
{
    public int Seq { get; set; }
    public string At { get; set; } = "";
    public string Kind { get; set; } = "";
    /// Display-ready, and written BY CODE from something observed. Never model narration: a step that
    /// claimed a search it never ran would be the same class of harm as a fabricated verdict.
    public string Text { get; set; } = "";
    public RunStepDetail? Detail { get; set; }
}

/// One agent (or deterministic stage) invocation, and everything observed while it ran.
///
/// Lives in the `runs` container, NOT `record`: this is high-volume append-only telemetry and it must
/// never appear in a query that reads project state.
public sealed class RunDoc
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Stage { get; set; } = "";
    /// null ⇒ a deterministic stage. The UI must not imply a model was involved.
    public string? Agent { get; set; }
    /// "{cas}|{componentId}" on a regulatory child run.
    public string? Subject { get; set; }
    /// Set on regulatory children, so the UI groups them explicitly rather than inferring from timing.
    public string? ParentRunId { get; set; }
    public string Trigger { get; set; } = RunTriggers.Pipeline;
    public string StartedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string? EndedAt { get; set; }
    public string Outcome { get; set; } = RunOutcome.Running;
    public string? Error { get; set; }
    public List<RunStep> Steps { get; set; } = [];

    public RunStep Append(string kind, string text, RunStepDetail? detail = null)
    {
        var step = new RunStep
        {
            Seq = Steps.Count + 1,
            At = DateTimeOffset.UtcNow.ToString("O"),
            Kind = kind,
            Text = text,
            Detail = detail,
        };
        Steps.Add(step);
        return step;
    }
}
