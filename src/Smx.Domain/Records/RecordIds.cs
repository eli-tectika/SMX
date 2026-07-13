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
}
