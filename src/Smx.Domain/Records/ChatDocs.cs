namespace Smx.Domain.Records;

public static class ChatStatus
{
    public const string Pending = "pending";
    public const string Answered = "answered";
    public const string Failed = "failed";
}

public static class ChatRoles
{
    public const string Operator = "operator";
    public const string Agent = "agent";
}

/// One thing the agent did during a chat turn, for the UI's tool-call/citation trail (design §5:
/// "the reply carries its tool-call/citation trail"). `RecordId` is set when the call WROTE something —
/// that is the audit link from a sentence in the chat to the record it changed.
public sealed record ChatToolCall(string Tool, string Summary, string? RecordId);

/// The operator's message to the current stage's agent, scoped to (project, stage). Chat is per-stage,
/// not one global thread: agents do not share a conversation (Law 9), so neither do their threads.
///
/// It rides the record bus like everything else — the backend cannot run an agent, so writing this doc
/// IS the dispatch. And because the thread lives in the record rather than in an in-memory agent session,
/// the conversation survives a multi-day re-entry (Law 6) and an orchestrator restart.
public sealed class ChatMessageDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.ChatMessage;
    public required string Stage { get; set; }
    public required string Text { get; set; }
    public string Status { get; set; } = ChatStatus.Pending;
    public string? Error { get; set; }
    /// ISO-8601, ALWAYS via DateTimeOffset...ToString("O"). The thread is ordered by a LEXICOGRAPHIC sort
    /// on this field (a server-side Cosmos ORDER BY on a string), which is only chronological while every
    /// writer uses the same fixed-width format. Mixing "O" with a whole-second "…Z" silently misorders the
    /// conversation — and a transcript out of order is a transcript that lies about who said what first.
    public required string CreatedAt { get; set; }
}

/// The agent's reply. Its id is derived from the message's key, so a change-feed redelivery upserts the
/// same reply rather than appending a second one.
public sealed class ChatReplyDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.ChatReply;
    public required string Stage { get; set; }
    public required string MessageId { get; set; }        // the ChatMessageDoc this answers
    public required string Text { get; set; }
    public List<ChatToolCall> ToolCalls { get; set; } = [];
    public required string CreatedAt { get; set; }        // see ChatMessageDoc.CreatedAt — same rule
}

/// One turn in a persisted chat thread — either side of it. The thread is a mixed sequence of messages and
/// replies, so the store merges the two doc types into this single shape the caller can order and render.
/// `Role` is <see cref="ChatRoles.Operator"/> | <see cref="ChatRoles.Agent"/>.
public sealed record ChatTurn(string Role, string Text, string CreatedAt, IReadOnlyList<ChatToolCall> ToolCalls);
