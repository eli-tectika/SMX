namespace Smx.Domain.Records;

public static class RevisionStatus
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Failed = "failed";
}

/// The operator's "change X because Y" (design §4, Law 4: no direct edits to agent output — you tell
/// the agent WHY and it re-runs). It rides the record bus like everything else: the backend cannot run
/// an agent, so writing this doc IS the dispatch — the change feed picks it up and the orchestrator
/// re-runs the stage.
public sealed class RevisionDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.Revision;
    public required string Stage { get; set; }            // RevisionEffects.IsRevisable(stage) must hold
    public required string Target { get; set; }           // what to change, in the operator's words
    /// Why. Non-empty, always — it is both the justification for mutating an analytical result and the
    /// seed of the Learned Conclusion. A revision without a reason is a silent edit that teaches nothing.
    public required string Reason { get; set; }
    /// Which verdict to re-run. Required for a `regulatory` revision (a verdict is per substance ×
    /// component, so a revision must name one); ignored for `discovery`, which re-runs holistically.
    public string? Cas { get; set; }
    public string? ComponentId { get; set; }
    public string Status { get; set; } = RevisionStatus.Pending;
    public string? Error { get; set; }
    public string? ConclusionId { get; set; }             // the Learned Conclusion this revision produced
    public required string CreatedAt { get; set; }        // ISO-8601 (caller-supplied; domain has no clock)
    public string? AppliedAt { get; set; }
}
