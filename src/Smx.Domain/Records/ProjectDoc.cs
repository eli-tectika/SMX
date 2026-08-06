using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smx.Domain.Records;

/// The stage statuses, as strings because that is what is on the wire and in Cosmos. Named constants
/// because each is compared across three projects and a typo in any of them is not a compile error —
/// it is a stage that silently never advances, or one that advances when it should not.
public static class StageStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string NeedsReview = "needs-review";
    public const string Done = "done";

    // THE PARK STATES ARE DELETED (execution-core design §8/D10, implemented 2026-08-06). There was an
    // `awaiting-RE` on Regulatory, `awaiting-physics` and `awaiting-operator` on Dosing, and `awaiting-VP`
    // on Decision. The pipeline runs end to end on the best data it has; what is outstanding is a SIGNATURE
    // (on the GateDoc) or BETTER INPUT (a DosingDoc provisional reason), never a stalled computation.
    //
    // Do not reintroduce one without reading §8. A stage status says whether its AGENT RAN. What guards
    // procurement lives on the irreversible act instead: POST /orders refuses over an unsigned VP gate, an
    // unopened flagged verdict (EvidenceReview.Outstanding), a provisional dosing or a missing MSDS.
    // NoParkStatusesTests fails the build if a park constant reappears here.

    // `awaiting-confirmation` is deleted too. The line between "the agent created something" and "the
    // analysis is running" is now ProjectDoc.AnalysisStartedAt — a project-level fact, not a stage status,
    // because a stage status should only ever answer "did this stage's agent run".
}

public sealed class StageState
{
    /// See <see cref="StageStatus"/> for the named constants (pending, running, failed, needs-review,
    /// done); this stays a plain string
    /// because that is what is on the wire and in Cosmos.
    public string Status { get; set; } = StageStatus.Pending;
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

/// One requirement change, with what it cost.
///
/// <paramref name="Rerun"/> is recorded rather than re-derived on read: <see cref="RerunScope"/> may
/// legitimately change as the pipeline changes, and the log has to say what was ACTUALLY re-run at the time,
/// not what today's map would have chosen. <paramref name="VoidedSignatures"/> is the same reasoning — a
/// signature that was voided is a fact about that day, and the gate record alone cannot say which amendment
/// did it.
public sealed record Amendment(
    string At, string ComponentId, string Field, string From, string To, string Reason,
    IReadOnlyList<string> Rerun, IReadOnlyList<string> VoidedSignatures);

public sealed class ProjectDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Project;
    public required string Client { get; set; }
    public required string Product { get; set; }
    public JsonElement Payload { get; set; } // the POST /projects body, verbatim
    public Dictionary<string, StageState> Stages { get; set; } = new();
    public string CreatedAt { get; set; } = "";

    /// WHEN THE OPERATOR AUTHORISED THE ANALYSIS. Null until POST /projects/{id}/start.
    ///
    /// This is the whole of "the agent may create a project, only the operator may start one" (design
    /// §2.3), and it is a PROJECT-LEVEL FACT rather than a stage status on purpose. It used to be
    /// `awaiting-confirmation` on the intake stage, which conflated two different things: whether intake
    /// had run, and whether the operator had authorised anything. Intake is now transcription of what the
    /// operator just dictated in the interview — it runs at creation, during "Setting up the project…" —
    /// so the stage status can go back to meaning only "did this stage's agent run".
    ///
    /// PipelineRunner reads it to stop a pass after intake: everything downstream of intake is real
    /// analysis, and none of it starts until this is set. The signature therefore moves from BEFORE the
    /// operator has seen anything the agent produced to AFTER they have read the brief.
    ///
    /// Serialized even when null ([JsonIgnore(Never)]) so the UI reads "not started" off the wire rather
    /// than inferring it from an absent key.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AnalysisStartedAt { get; set; }

    /// Every requirement the operator changed after intake, in order.
    ///
    /// A requirement change that leaves no trace is indistinguishable, afterwards, from an analysis that was
    /// always wrong: the record shows markets the agent never screened against and nothing says when they
    /// arrived or why. The REASON is the point — the same instinct as a Learned Conclusion — so it is
    /// mandatory at the door, and `From`/`To` are kept so the log reads as a history rather than a list of
    /// edits.
    public List<Amendment> Amendments { get; set; } = [];

    /// `analysisStarted` DEFAULTS to true — i.e. writing the doc makes the project runnable, exactly as
    /// before. Only the INTERVIEW AGENT passes false, because it is the only caller that is a language
    /// model; every other caller (POST /projects with a full payload, tools/Smx.Eval, the backend tests)
    /// is a human or a harness explicitly asking for a run, and is unchanged by construction.
    ///
    /// This is the same seam the old `intakeStatus: AwaitingConfirmation` argument was, moved onto the
    /// project where it belongs. `intakeStatus` survives so a test can still seed a project mid-journey.
    public static ProjectDoc Create(string projectId, string client, string product, JsonElement payload,
        string intakeStatus = StageStatus.Pending, bool analysisStarted = true) => new()
    {
        Id = projectId, ProjectId = projectId, Client = client, Product = product,
        Payload = payload.Clone(),
        AnalysisStartedAt = analysisStarted ? DateTimeOffset.UtcNow.ToString("O") : null,
        Stages = new()
        {
            [Records.Stages.Intake] = new StageState { Status = intakeStatus },
            // Pool (agent-proposed candidate pool) and Background (XRF filter, currently pass-through) sit
            // between Intake and Discovery. Backend-only — the UI spine does not render them.
            [Records.Stages.Pool] = new StageState(),
            [Records.Stages.Background] = new StageState(),
            [Records.Stages.Discovery] = new StageState(),
            [Records.Stages.Regulatory] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
            [Records.Stages.Dosing] = new StageState(),
            [Records.Stages.Decision] = new StageState(),
        },
    };
}
