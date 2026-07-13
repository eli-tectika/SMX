namespace Smx.Domain.Records;

public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
    public const string Gate = "gate";
    public const string Revision = "revision";
    public const string ChatMessage = "chat-message";
    public const string ChatReply = "chat-reply";
}

public static class Stages
{
    public const string Intake = "intake";
    public const string Discovery = "discovery";
    public const string Regulatory = "regulatory";
    public const string Matrix = "matrix";
}

public static class RecordIds
{
    public static string Constraints(string projectId) => $"{projectId}|constraints";
    public static string Candidates(string projectId) => $"{projectId}|candidates";
    public static string Verdict(string projectId, string cas, string componentId) => $"{projectId}|verdict|{cas}|{componentId}";
    public static string Matrix(string projectId) => $"{projectId}|matrix";
    public static string Gate(string projectId, string gateType) => $"{projectId}|gate|{gateType}";

    /// `key` is a per-request unique suffix, not a hash of the content: two revisions of the same target
    /// are two distinct decisions and both belong in the audit trail. Change-feed idempotency comes from
    /// RevisionDoc.Status, not from the id.
    public static string Revision(string projectId, string stage, string key) =>
        $"{projectId}|revision|{stage}|{key}";

    /// `key` is a per-message unique suffix, not a hash of the text: two identical questions asked at
    /// different times are two distinct turns and both belong in the transcript. A colliding key silently
    /// overwrites an earlier turn on upsert — and its reply with it.
    public static string ChatMessage(string projectId, string stage, string key) =>
        $"{projectId}|chat-message|{stage}|{key}";

    /// Derived from the MESSAGE's key, deliberately: the reply is a function of the message, so an
    /// at-least-once change feed re-delivering the message upserts one reply instead of appending a
    /// second one to the transcript.
    public static string ChatReply(string projectId, string stage, string key) =>
        $"{projectId}|chat-reply|{stage}|{key}";
}
