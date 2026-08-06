using System.Text.Json;

namespace Smx.Domain.Records;

/// The stage statuses, as strings because that is what is on the wire and in Cosmos. Named constants
/// because `awaiting-confirmation` is compared in three projects and a typo in any of them silently
/// means "this project never starts" — or, worse, "this project starts without anyone confirming it".
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
    // Do not reintroduce one without reading §8. A stage status says whether its AGENT RAN. Law 9 is
    // enforced where it belongs — CompliantSet reads only the operator's Determination, and the two
    // irreversible acts (the compliance-package export, POST /orders) refuse over an unsigned gate or a
    // provisional dosing. NoParkStatusesTests fails the build if a park constant reappears here.

    /// Intake only. The project EXISTS and its dossier is written, but no agent has run and none will
    /// until POST /projects/{id}/start flips this to Pending. This constant is the line between
    /// "the agent created something" and "the analysis is running" — see design §2.3.
    public const string AwaitingConfirmation = "awaiting-confirmation";
}

public sealed class StageState
{
    /// See <see cref="StageStatus"/> for the named constants (pending, running, the three awaiting-*
    /// park states, failed, needs-review, done, awaiting-confirmation); this stays a plain string
    /// because that is what is on the wire and in Cosmos.
    public string Status { get; set; } = StageStatus.Pending;
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

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

    /// `intakeStatus` DEFAULTS to Pending — i.e. writing the doc dispatches intake, exactly as before.
    /// Only the interview agent passes AwaitingConfirmation, because it is the only caller that is a
    /// language model. Every existing caller (POST /projects with a full payload, tools/Smx.Eval, the
    /// backend tests) is unchanged by construction.
    public static ProjectDoc Create(string projectId, string client, string product, JsonElement payload,
        string intakeStatus = StageStatus.Pending) => new()
    {
        Id = projectId, ProjectId = projectId, Client = client, Product = product,
        Payload = payload.Clone(),
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
            [Records.Stages.Cost] = new StageState(),
            [Records.Stages.Decision] = new StageState(),
        },
    };
}
