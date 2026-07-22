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
    public const string Decision = "decision";
    public const string Gate = "gate";
    public const string Revision = "revision";
    public const string ChatMessage = "chat-message";
    public const string ChatReply = "chat-reply";
    /// The need-driven marker pool: an agent's proposed candidate elements/forms, produced BEFORE Discovery
    /// from the project's need alone (no XRF filtering). See the pool subsystem design.
    public const string Pool = "pool";
}

public static class Stages
{
    public const string Intake = "intake";
    /// The need-driven pool proposal (an agent) and the XRF background filter. Both sit between Intake and
    /// Discovery and are DELIBERATELY absent from `All` below: they are backend-only stages, neither
    /// operator-chattable nor rendered in the UI spine. `Background` is currently a pass-through (XRF deferred).
    public const string Pool = "pool";
    public const string Background = "background";
    public const string Discovery = "discovery";
    public const string Regulatory = "regulatory";
    public const string Matrix = "matrix";
    public const string Dosing = "dosing";
    public const string Cost = "cost";
    public const string Decision = "decision";

    /// Every CHATTABLE stage — and therefore every stage the operator can TALK to (ChatEndpoints validates
    /// against it). Hand-maintained beside the constants, so ChatEndpointsTests reflects over the class and
    /// fails if the two ever part company: a stage added above but not here is silently un-chattable, and
    /// nobody finds out until an operator gets a 422 for a stage the product says exists. Pool and Background
    /// are intentionally excluded — they are hidden, non-chattable stages (see their doc-comment above).
    public static readonly string[] All = [Intake, Discovery, Regulatory, Matrix, Dosing, Cost, Decision];
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

    /// One decision doc per project, same singular-per-project rationale as Dosing/Cost — the per-component
    /// split lives INSIDE the doc (ComponentDecision.ComponentId).
    public static string Decision(string projectId) => $"{projectId}|decision";

    /// One pool doc per project — the proposed candidate pool, singular like Dosing/Cost/Decision so an
    /// at-least-once redelivery upserts one doc. The per-component split lives INSIDE it (PoolSuggestion.Component).
    public static string Pool(string projectId) => $"{projectId}|pool";

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
