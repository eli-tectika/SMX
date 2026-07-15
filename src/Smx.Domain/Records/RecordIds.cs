namespace Smx.Domain.Records;

public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
    public const string Dosing = "dosing";
    public const string Cost = "cost";
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

    /// Every stage there is — and therefore every stage the operator can TALK to (ChatEndpoints validates
    /// against it). Hand-maintained beside the constants, so ChatEndpointsTests reflects over the class and
    /// fails if the two ever part company: a stage added above but not here is silently un-chattable, and
    /// nobody finds out until an operator gets a 422 for a stage the product says exists.
    public static readonly string[] All = [Intake, Discovery, Regulatory, Matrix];
}

public static class RecordIds
{
    public static string Constraints(string projectId) => $"{projectId}|constraints";
    public static string Candidates(string projectId) => $"{projectId}|candidates";
    public static string Verdict(string projectId, string cas, string componentId) => $"{projectId}|verdict|{cas}|{componentId}";
    public static string Matrix(string projectId) => $"{projectId}|matrix";

    /// One dosing doc and one cost doc per project — both are whole-project rollups (the PER-COMPONENT split
    /// lives INSIDE them, in PpmWindow.ComponentId and MarkerCode.ComponentId), so neither id takes a
    /// component. Singular ids also make the change feed's at-least-once redelivery an idempotent upsert
    /// rather than a second document.
    public static string Dosing(string projectId) => $"{projectId}|dosing";
    public static string Cost(string projectId) => $"{projectId}|cost";

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
