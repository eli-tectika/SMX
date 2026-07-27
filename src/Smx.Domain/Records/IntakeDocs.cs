using Smx.Domain.Intake;

namespace Smx.Domain.Records;

public static class IntakeSessionStatus
{
    public const string Interviewing = "interviewing";
    public const string Created = "created";
    public const string Abandoned = "abandoned";
}

public static class AttachmentStatus
{
    public const string Extracted = "extracted";
    /// We have no extractor for this format. The agent is TOLD, by name and type, and asks the
    /// operator what the file shows. An unreadable file is a visible fact, never silence.
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
}

public sealed class InterviewTurn
{
    public required string Role { get; set; }   // "operator" | "agent"
    public required string Text { get; set; }
    public List<string> ToolCalls { get; set; } = [];
    /// ALWAYS DateTimeOffset.UtcNow.ToString("O"). This is the transcript's SORT KEY and it is compared
    /// LEXICOGRAPHICALLY, which is only chronological while every writer uses the same fixed-width
    /// format. Two writers disagreeing here makes the transcript lie about who said what first.
    public required string CreatedAt { get; set; }
}

public sealed class SessionAttachment
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string BlobPath { get; set; } = "";
    public string? TextBlobPath { get; set; }
    public string Status { get; set; } = AttachmentStatus.Unsupported;
    public string? Error { get; set; }
}

/// The interview scratchpad. Lives in its OWN Cosmos container (`intake-sessions`, PK /sessionId) and
/// never in `record`.
///
/// That separation is structural, not organisational. `record` holds the per-project analytical bus —
/// the documents every stage reads as its input and every export cites. A half-finished interview
/// scratchpad sitting among them would be a document each of those readers must be TAUGHT to ignore, a
/// rule that holds right up until someone forgets it. A separate container makes the mistake unavailable
/// rather than merely discouraged.
public sealed class IntakeSessionDoc
{
    public required string Id { get; set; }
    public required string SessionId { get; set; }
    public string Status { get; set; } = IntakeSessionStatus.Interviewing;
    public string Client { get; set; } = "";
    public string Product { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<InterviewTurn> Turns { get; set; } = [];
    public List<SessionAttachment> Attachments { get; set; } = [];
    public List<DossierEntry> Dossier { get; set; } = [];
    public List<ComponentSpec> ProposedComponents { get; set; } = [];
    /// Set by create_project. Its presence makes the tool IDEMPOTENT: the change feed and the model
    /// both retry, and a retried create must return the existing project rather than mint a second one.
    public string? CreatedProjectId { get; set; }
    public required string CreatedAt { get; set; }
    public string UpdatedAt { get; set; } = "";
    /// Cosmos TTL, seconds. 30 days: abandoned drafts expire on their own, because nobody will ever
    /// go and delete them. The blobs outlive this deliberately — see design §5.3.
    public int Ttl { get; set; } = 60 * 60 * 24 * 30;
}

/// The deliverable: what create_project writes into the project, what the intake screen renders, and
/// what downstream agents read.
///
/// It carries the TRANSCRIPT, not merely the conclusions. When a Regulatory verdict later hinges on the
/// operator having said the label adhesive is water-based, that sentence is in the record, attributable,
/// beside the dossier row it produced. Written once; it is not a stage output and triggers no dispatch
/// (RecordDocRouter ignores `intake-brief`, and a test pins that in Task 6).
public sealed class IntakeBriefDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.IntakeBrief;
    public required string SessionId { get; set; }
    public string Summary { get; set; } = "";
    public List<DossierEntry> Dossier { get; set; } = [];
    public List<ComponentSpec> Components { get; set; } = [];
    public List<SessionAttachment> Attachments { get; set; } = [];
    public List<InterviewTurn> Transcript { get; set; } = [];
    public required string CreatedAt { get; set; }
}
