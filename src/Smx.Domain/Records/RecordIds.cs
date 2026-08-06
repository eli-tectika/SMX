namespace Smx.Domain.Records;

public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
    public const string Dosing = "dosing";
    public const string Decision = "decision";
    public const string Gate = "gate";
    public const string Revision = "revision";
    public const string ChatMessage = "chat-message";
    public const string ChatReply = "chat-reply";
    public const string IntakeBrief = "intake-brief";
    /// The need-driven marker pool: an agent's proposed candidate elements/forms, produced BEFORE Discovery
    /// from the project's need alone (no XRF filtering). See the pool subsystem design.
    public const string Pool = "pool";
}

public static class Stages
{
    public const string Intake = "intake";
    /// The need-driven pool proposal. It is DISCOVERY'S FIRST PASS, not an agent of its own — `PoolAgent`
    /// merged into `DiscoveryAgent` on 2026-08-06 (redesign spec §16.3), because two agents implied two
    /// opinions about one operator move.
    ///
    /// THE STAGE SURVIVES THE AGENT, deliberately. A stage is a unit of work the record can address —
    /// re-run, reset, skip, report — and the pool is all four: it skips when the operator supplied an element
    /// pool or provided candidates, it resets on its own when an amendment changes the material, and it
    /// produces its own artifact (PoolDoc), which is what the Discovery screen shows as what is being
    /// corroborated. Folding it into `discovery` would buy one fewer identifier and cost the ability to
    /// re-corroborate without re-proposing. It stays in `All` below, so its thread is postable and the
    /// operator can still argue with the proposal.
    public const string Pool = "pool";
    /// The XRF background filter, currently a pass-through (XRF deferred). DELIBERATELY absent from `All`:
    /// there is no agent to talk to, so a thread on it could only be a conversation with nobody.
    public const string Background = "background";
    public const string Discovery = "discovery";
    public const string Regulatory = "regulatory";
    public const string Matrix = "matrix";
    public const string Dosing = "dosing";
    public const string Decision = "decision";

    /// Every CHATTABLE stage — and therefore every stage the operator can TALK to (ChatEndpoints and
    /// ThreadEndpoints both validate against it). Hand-maintained beside the constants, so ChatEndpointsTests
    /// reflects over the class and fails if the two ever part company: a stage added above but not here is
    /// silently un-chattable, and nobody finds out until an operator gets a 422 for a stage the product says
    /// exists. Background is the one exclusion — a pass-through with no agent behind it.
    ///
    /// Listed in pipeline order, but do NOT read adjacency off this array: `Pool` is a stage that legitimately
    /// SKIPS (provided candidates, an operator element pool), so it can sit at `pending` on a project that ran
    /// to completion. <see cref="Spine"/> is the ordered list for anything that reasons about what comes next.
    public static readonly string[] All = [Intake, Pool, Discovery, Regulatory, Matrix, Dosing, Decision];

    /// The OPERATOR-FACING journey, in order — the eight-stage spine the UI renders and the dashboard's
    /// "ready to continue" walks. Pool and Background are excluded on purpose: both can be skipped by the
    /// runner without ever being stamped, so a neighbour-is-done rule over them would either advertise a
    /// stage the operator has no screen for, or stall the walk at a stage nothing will ever complete.
    public static readonly string[] Spine = [Intake, Discovery, Regulatory, Matrix, Dosing, Decision];
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

    /// One decision doc per project, same singular-per-project rationale as Dosing — the per-component
    /// split lives INSIDE the doc (ComponentDecision.ComponentId).
    public static string Decision(string projectId) => $"{projectId}|decision";

    /// One pool doc per project — the proposed candidate pool, singular like Dosing/Decision so an
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

    /// One brief per project — it is written once, by create_project, and never revised. A singular id
    /// makes the change feed's at-least-once redelivery an idempotent upsert rather than a second doc.
    public static string IntakeBrief(string projectId) => $"{projectId}|intake-brief";

    /// The session id doubles as the Cosmos item id AND the partition key, so it must be id-safe
    /// ([A-Za-z0-9_-]+): Cosmos rejects '/', '\', '?' and '#' with a 400 no in-memory store produces.
    /// `N` format = hex only, so this is safe by construction rather than by convention.
    public static string NewIntakeSessionId() => $"isx-{Guid.NewGuid():N}"[..16];

    /// Id-safe by construction, like NewIntakeSessionId: it becomes a BLOB PATH SEGMENT, and `N` format
    /// is hex only, so no separator can appear in it however it is concatenated.
    public static string NewAttachmentId() => $"att-{Guid.NewGuid():N}"[..16];
}
