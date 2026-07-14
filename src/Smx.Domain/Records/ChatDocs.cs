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
///
/// `Id` is the SOURCE DOC's id (the ChatMessageDoc's or the ChatReplyDoc's). It is here so the dispatcher can
/// render the thread as PRIOR conversation only: the message being answered is already in the record by the
/// time its turn runs — writing it IS the dispatch — so the turn that answers it has to exclude it by id.
/// By ID, and never by "everything before CreatedAt": two turns can share a timestamp (that is precisely why
/// ChatTurns.InOrder needs a tiebreak), so a time-based predicate silently drops or keeps the wrong turn on
/// the day two writes land on one tick.
public sealed record ChatTurn(string Id, string Role, string Text, string CreatedAt, IReadOnlyList<ChatToolCall> ToolCalls);

public static class ChatTurns
{
    /// The canonical order of a merged thread. It lives HERE, in one place, because both record stores have
    /// to produce byte-identical transcripts — two independent sorts agreeing is a coincidence, and a
    /// coincidence is what the fake would certify while production quietly disagreed.
    ///
    /// Ordinal, because the Cosmos-side comparison it stands in for is ordinal (a culture-sensitive compare
    /// would differ on some machines and not others). The tiebreak matters because a reply can carry the SAME
    /// CreatedAt as the message it answers (same clock tick, or a writer that rounds to the second): without
    /// it the winner is decided by enumeration order, and an answer printed above its own question is a
    /// transcript that lies about who said what first.
    public static IReadOnlyList<ChatTurn> InOrder(IEnumerable<ChatTurn> turns) =>
        turns
            .OrderBy(t => t.CreatedAt, StringComparer.Ordinal)
            .ThenBy(t => t.Role == ChatRoles.Operator ? 0 : 1)
            .ToList();
}
