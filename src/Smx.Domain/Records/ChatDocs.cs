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
    /// ISO-8601, ALWAYS via DateTimeOffset...ToString("O"). This field is the thread's SORT KEY — not only for
    /// this message but for the reply that answers it, which is ANCHORED to this value (ChatTurns.InOrder) —
    /// and it is compared LEXICOGRAPHICALLY (ordinal), which is only chronological while every writer uses the
    /// same fixed-width format. Mixing "O" with a whole-second "…Z" silently misorders the conversation, and a
    /// transcript out of order is a transcript that lies about who said what first.
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
    /// When the turn ENDED and this reply was written — tens of seconds after the message it answers, with a
    /// tool loop in between. Same "O" rule as ChatMessageDoc.CreatedAt, but deliberately NOT this turn's sort
    /// key: the thread positions a reply under the message it answers (ChatTurns.InOrder), because the operator
    /// can post again while the turn is still running. This value stays truthful about the WRITE, which is what
    /// the audit trail needs; it is only the fallback sort key for an orphan reply whose message is gone.
    public required string CreatedAt { get; set; }
}

/// One turn in a persisted chat thread — either side of it. The thread is a mixed sequence of messages and
/// replies, so ChatTurns.InOrder merges the two doc types into this single shape the caller renders.
/// `Role` is <see cref="ChatRoles.Operator"/> | <see cref="ChatRoles.Agent"/>.
///
/// `Id` is the SOURCE DOC's id (the ChatMessageDoc's or the ChatReplyDoc's). It is here so the dispatcher can
/// render the thread as PRIOR conversation only: the message being answered is already in the record by the
/// time its turn runs — writing it IS the dispatch — so the turn that answers it has to exclude it by id.
/// By ID, and never by "everything before CreatedAt": two turns can share a timestamp (that is precisely why
/// ChatTurns.InOrder needs a tiebreak), so a time-based predicate silently drops or keeps the wrong turn on
/// the day two writes land on one tick.
///
/// `CreatedAt` is when this turn was WRITTEN. It is not, by itself, the sort key — see ChatTurns.InOrder.
///
/// `Status`/`Error` carry the ChatMessageDoc's, and they are on the wire because a turn that FAILED must be
/// distinguishable from one still in flight. To an operator who cannot see the status both look like "no reply
/// yet", and the answer to "no reply yet" is to re-send — which mints a new message id, hence a new chat key,
/// hence a revision id that no longer content-addresses onto the first one. Two RevisionDocs, two stage
/// re-runs, two Learned Conclusions out of one operator instruction. The invisible failure is what walks the
/// operator across that line, so the failure is made visible.
///
/// An AGENT turn carries <see cref="ChatStatus.Answered"/> and never an error. It has no status of its own to
/// carry: a ChatReplyDoc is only ever written by a turn that COMPLETED (StageDispatcher writes no reply on the
/// failure path — the operator must never read a half-answer as the agent's word), so the reply's existence IS
/// the completion. A failed turn's error lives on the message, the only doc that turn wrote.
public sealed record ChatTurn(
    string Id, string Role, string Text, string CreatedAt, IReadOnlyList<ChatToolCall> ToolCalls,
    string Status, string? Error = null);

public static class ChatTurns
{
    /// The canonical order of a merged thread. It lives HERE, in one place, because both record stores have
    /// to produce byte-identical transcripts — two independent sorts agreeing is a coincidence, and a
    /// coincidence is what the fake would certify while production quietly disagreed. It takes the DOCS rather
    /// than pre-merged turns because the ordering needs both sides at once: a reply is positioned by the
    /// message it answers, and only the join can tell it which one that is.
    ///
    /// A REPLY IS ANCHORED TO ITS MESSAGE, NOT TO ITS OWN CLOCK. ChatReplyDoc.CreatedAt is stamped when the
    /// turn ENDS — after a tool loop that runs for tens of seconds — while the operator may post again at any
    /// moment. So this interleaving is ordinary, not exotic:
    ///
    ///     M1  10:00:00  "why is Ba tier A?"
    ///     M2  10:00:20  the operator, still waiting, adds "also check Hf"
    ///     R1  10:00:30  the answer to M1 lands
    ///
    /// Sorted on its own timestamp R1 falls BELOW M2, and the answer to the Ba question is positioned as the
    /// answer to the Hf question. That is what the UI shows — and, because the agent has no memory of its own,
    /// it is what ChatThread.Render hands the agent as the entire conversation on every turn thereafter. So the
    /// sort key is the ANCHOR: a message's own CreatedAt, and a reply's is its message's.
    ///
    /// CreatedAt itself is left strictly truthful. (Stamping the reply from the message plus an epsilon would
    /// order it correctly and then lie about when it was written — which is what the audit trail reads — and
    /// could still collide. It is the ORDER that is wrong here, so it is the ORDER that is fixed.)
    ///
    /// The tiebreaks, in order:
    ///   - ROLE: a reply shares its message's anchor EXACTLY, so the operator turn must come first. An answer
    ///     printed above its own question is a transcript that lies about who said what first.
    ///   - ID (ordinal): two MESSAGES can still share a timestamp (one clock tick, or a writer that rounds to
    ///     the second). Without a final tiebreak the winner is enumeration order — a Cosmos page order in one
    ///     store and a dictionary order in the other — so the same thread would render differently depending on
    ///     which store read it, which is the exact drift a single shared sort function exists to prevent.
    ///
    /// Ordinal throughout, because the Cosmos-side comparison it stands in for is ordinal (a culture-sensitive
    /// compare would differ on some machines and not others).
    public static IReadOnlyList<ChatTurn> InOrder(
        IEnumerable<ChatMessageDoc> messages, IEnumerable<ChatReplyDoc> replies)
    {
        var msgs = messages.ToList();

        // Indexer, not ToDictionary: a duplicate id cannot happen in either store (the id IS the key there),
        // and a throw would take down the whole thread over an invariant it is not this function's job to
        // police — the transcript is what the agent's memory is made of, so it fails soft, never shut.
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in msgs) anchors[m.Id] = m.CreatedAt;

        const int OperatorTurn = 0, AgentTurn = 1;
        return msgs
            .Select(m => (
                Anchor: m.CreatedAt,
                Rank: OperatorTurn,
                Turn: new ChatTurn(m.Id, ChatRoles.Operator, m.Text, m.CreatedAt, [], m.Status, m.Error)))
            .Concat(replies.Select(r => (
                // An ORPHAN reply — its message deleted, or the store's two queries not being one snapshot —
                // falls back to its own clock. It must still render, and deterministically: throwing loses the
                // whole thread, and dropping it silently deletes something the agent actually said.
                Anchor: anchors.TryGetValue(r.MessageId, out var anchor) ? anchor : r.CreatedAt,
                Rank: AgentTurn,
                Turn: new ChatTurn(r.Id, ChatRoles.Agent, r.Text, r.CreatedAt, r.ToolCalls, ChatStatus.Answered))))
            .OrderBy(t => t.Anchor, StringComparer.Ordinal)
            .ThenBy(t => t.Rank)
            .ThenBy(t => t.Turn.Id, StringComparer.Ordinal)
            .Select(t => t.Turn)
            .ToList();
    }
}
