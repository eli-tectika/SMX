using System.Text.Json;

namespace Smx.Domain.Records;

/// The stage statuses, as strings because that is what is on the wire and in Cosmos. Named constants
/// because `awaiting-confirmation` is compared in three projects and a typo in any of them silently
/// means "this project never starts" — or, worse, "this project starts without anyone confirming it".
public static class StageStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string AwaitingRe = "awaiting-RE";
    public const string Failed = "failed";
    public const string NeedsReview = "needs-review";
    public const string Done = "done";

    /// Intake only. The project EXISTS and its dossier is written, but no agent has run and none will
    /// until POST /projects/{id}/start flips this to Pending. This constant is the line between
    /// "the agent created something" and "the analysis is running" — see design §2.3.
    public const string AwaitingConfirmation = "awaiting-confirmation";
}

public sealed class StageState
{
    /// See <see cref="StageStatus"/> for the named constants; this stays a plain string because that is
    /// what is on the wire and in Cosmos.
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
            [Records.Stages.Discovery] = new StageState(),
            [Records.Stages.Regulatory] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
            [Records.Stages.Dosing] = new StageState(),
            [Records.Stages.Cost] = new StageState(),
        },
    };
}
