using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class InMemoryRecordStoreTests
{
    [Fact]
    public async Task Upserts_AreIdempotent_ByDocumentId()
    {
        var store = new InMemoryRecordStore();
        var v = new VerdictDoc { Id = RecordIds.Verdict("p1", "c1", "bottle"), ProjectId = "p1",
            Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" };
        await store.UpsertVerdictAsync(v);
        await store.UpsertVerdictAsync(v); // redelivery must be harmless
        Assert.Single(await store.GetVerdictsAsync("p1"));
    }

    [Fact]
    public async Task Queries_AreScopedToProject()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p2", "c1", "bottle"),
            ProjectId = "p2", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        Assert.Single(await store.GetVerdictsAsync("p1"));
        Assert.Null(await store.GetMatrixAsync("p1"));
    }

    [Fact]
    public async Task Candidates_UpsertThenGet_RoundTrips()
    {
        var store = new Smx.Domain.Tests.Fakes.InMemoryRecordStore();
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "r", [])],
        });
        var got = await store.GetCandidatesAsync("p1");
        Assert.NotNull(got);
        Assert.Single(got!.Substances);
    }

    [Fact]
    public async Task Gate_And_SingleVerdict_RoundTrip()
    {
        var store = new Smx.Domain.Tests.Fakes.InMemoryRecordStore();
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved" });
        var g = await store.GetGateAsync("p1", GateTypes.Regulatory);
        Assert.NotNull(g);
        Assert.Equal("approved", g!.Status);
        Assert.Null(await store.GetGateAsync("p1", "vp"));

        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p1", "cas1", "bottle"),
            ProjectId = "p1", Cas = "cas1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        var v = await store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.NotNull(v);
        Assert.Equal("Zr", v!.Element);
        Assert.Null(await store.GetVerdictAsync("p1", "nope", "bottle"));
    }
}

public class RevisionStoreTests
{
    private static RevisionDoc Rev(string project, string key, string createdAt) => new()
    {
        Id = RecordIds.Revision(project, Stages.Discovery, key), ProjectId = project,
        Stage = Stages.Discovery, Target = "Ba tier", Reason = "overlaps Ti", CreatedAt = createdAt,
    };

    [Fact]
    public async Task GetRevisions_ReturnsThisProjectsRevisions_OldestFirst()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertRevisionAsync(Rev("proj-1", "b", "2026-07-13T02:00:00Z"));
        await store.UpsertRevisionAsync(Rev("proj-1", "a", "2026-07-13T01:00:00Z"));
        await store.UpsertRevisionAsync(Rev("proj-2", "c", "2026-07-13T03:00:00Z"));

        var revisions = await store.GetRevisionsAsync("proj-1");

        Assert.Equal(2, revisions.Count);
        Assert.Equal(["2026-07-13T01:00:00Z", "2026-07-13T02:00:00Z"], revisions.Select(r => r.CreatedAt));
    }

    [Fact]
    public async Task GetRevisions_OnColdStart_ReturnsEmpty_NotNull() =>
        Assert.Empty(await new InMemoryRecordStore().GetRevisionsAsync("proj-nothing"));

    /// The other doc types share the project's partition. The fake excludes them with a CLR type check
    /// (`OfType<RevisionDoc>()`) while Cosmos excludes them with `WHERE root["type"] = "revision"` — the one
    /// place the twins use genuinely different mechanisms, so pin the behaviour they must agree on.
    [Fact]
    public async Task GetRevisions_ExcludesOtherDocTypesInTheSamePartition()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertRevisionAsync(Rev("proj-1", "a", "2026-07-13T01:00:00Z"));
        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("proj-1", "c1", "bottle"),
            ProjectId = "proj-1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("proj-1", GateTypes.Regulatory),
            ProjectId = "proj-1", GateType = GateTypes.Regulatory, Status = "approved" });
        await store.UpsertCandidatesAsync(new CandidatesDoc { Id = RecordIds.Candidates("proj-1"),
            ProjectId = "proj-1", Substances = [] });

        var only = Assert.Single(await store.GetRevisionsAsync("proj-1"));
        Assert.Equal(RecordTypes.Revision, only.Type);
    }

    [Fact]
    public async Task UpsertRevision_ReplacesByIdSoChangeFeedRedeliveryIsHarmless()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertRevisionAsync(Rev("proj-1", "a", "2026-07-13T01:00:00Z"));

        // A DISTINCT object with the same id — the dispatcher re-reads the doc from the store before it
        // marks it applied, so replacement must work by id, not by having mutated the caller's reference.
        var applied = Rev("proj-1", "a", "2026-07-13T01:00:00Z");
        applied.Status = RevisionStatus.Applied;
        await store.UpsertRevisionAsync(applied);

        var only = Assert.Single(await store.GetRevisionsAsync("proj-1"));
        Assert.Equal(RevisionStatus.Applied, only.Status);
    }
}

public class ChatStoreTests
{
    private static ChatMessageDoc Msg(string project, string stage, string key, string at) => new()
    {
        Id = RecordIds.ChatMessage(project, stage, key), ProjectId = project,
        Stage = stage, Text = $"msg {key}", CreatedAt = at,
    };
    private static ChatReplyDoc Reply(string project, string stage, string key, string at) => new()
    {
        Id = RecordIds.ChatReply(project, stage, key), ProjectId = project, Stage = stage,
        MessageId = RecordIds.ChatMessage(project, stage, key), Text = $"reply {key}", CreatedAt = at,
    };

    [Fact]
    public async Task GetChatThread_IsScopedToOneStage_AndOrderedOldestFirst()
    {
        // Chat is PER-STAGE (Law 9: agents don't share a conversation, so neither do their threads).
        // A Discovery thread must never leak into the Regulatory agent's context.
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "b", "2026-07-13T02:00:00Z"));
        await store.UpsertChatReplyAsync(Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:30:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Regulatory, "c", "2026-07-13T03:00:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-2", Stages.Discovery, "d", "2026-07-13T04:00:00Z"));

        var thread = await store.GetChatThreadAsync("proj-1", Stages.Discovery);

        Assert.Equal(
            ["2026-07-13T01:00:00Z", "2026-07-13T01:30:00Z", "2026-07-13T02:00:00Z"],
            thread.Select(t => t.CreatedAt));
    }

    [Fact]
    public async Task GetChatThread_OnColdStart_ReturnsEmpty_NotNull() =>
        Assert.Empty(await new InMemoryRecordStore().GetChatThreadAsync("proj-nothing", Stages.Discovery));

    [Fact]
    public async Task UpsertChatReply_ReplacesById_SoRedeliveryDoesNotAppendASecondReply()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertChatReplyAsync(Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));

        // A DISTINCT object with the same id — the dispatcher re-reads docs from the store, so replacement
        // must work by id, not by having mutated the caller's reference.
        var second = Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z");
        second.Text = "revised reply";
        await store.UpsertChatReplyAsync(second);

        var only = Assert.Single(await store.GetChatThreadAsync("proj-1", Stages.Discovery));
        Assert.Equal("agent", only.Role);
        Assert.Equal("revised reply", only.Text);
    }

    [Fact]
    public async Task GetChatThread_ExcludesOtherDocTypesInTheSamePartition()
    {
        // Half the guarantee. This pins the FAKE's half — that a Revision sharing the project's partition
        // never surfaces as a chat turn. It cannot pin Cosmos's half: the fake filters by CLR type
        // (.OfType<ChatMessageDoc>()) and Cosmos filters by the `type` string field in SQL, and nothing in a
        // dictionary emits SQL. The Cosmos half is pinned in CosmosQueryTextTests, which asserts on the
        // actual query text; the two tests are only meaningful together.
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));
        await store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = RecordIds.Revision("proj-1", Stages.Discovery, "r1"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Target = "t", Reason = "r", CreatedAt = "2026-07-13T01:00:00Z",
        });

        Assert.Single(await store.GetChatThreadAsync("proj-1", Stages.Discovery));
    }

    /// The dispatcher point-reads the message it was handed by the change feed to re-check its status
    /// before answering; a missing id must come back null, not throw (the Cosmos twin swallows a 404).
    [Fact]
    public async Task GetChatMessage_PointReads_AndIsNullWhenAbsent()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));

        var got = await store.GetChatMessageAsync("proj-1", RecordIds.ChatMessage("proj-1", Stages.Discovery, "a"));
        Assert.NotNull(got);
        Assert.Equal(ChatStatus.Pending, got!.Status);
        Assert.Null(await store.GetChatMessageAsync("proj-1", RecordIds.ChatMessage("proj-1", Stages.Discovery, "zz")));
    }

    /// This is the only point-read that takes a raw id rather than deriving it, so it is the only one where a
    /// mismatched (projectId, id) pair is constructible. Cosmos reads inside a partition and would return
    /// null; the fake must not hand back another project's message.
    [Fact]
    public async Task GetChatMessage_IsPartitionScoped_AndWillNotCrossProjects()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-2", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));

        Assert.Null(await store.GetChatMessageAsync(
            "proj-1", RecordIds.ChatMessage("proj-2", Stages.Discovery, "a")));
    }

    /// The dispatcher's whole idempotency guard is a read-modify-WRITE: point-read the message, answer it,
    /// set Status = answered, upsert. If the store handed back the live stored object, a dispatcher that
    /// forgot that last upsert would still look correct here — while in Cosmos the message stays `pending`,
    /// the at-least-once change feed redelivers it, and the turn re-runs, queueing a second revision. So the
    /// fake must snapshot on write and hand back a fresh graph on read, exactly as a JSON round-trip does.
    [Fact]
    public async Task Store_SnapshotsOnWrite_AndCopiesOnRead_LikeAJsonRoundTrip()
    {
        var store = new InMemoryRecordStore();
        var msg = Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z");
        await store.UpsertChatMessageAsync(msg);

        // Mutating the caller's reference AFTER the upsert must not reach the store: no upsert, no write.
        msg.Status = ChatStatus.Answered;
        var reread = await store.GetChatMessageAsync("proj-1", msg.Id);
        Assert.Equal(ChatStatus.Pending, reread!.Status);

        // And mutating what a read handed back must not reach the store either.
        reread.Status = ChatStatus.Failed;
        Assert.Equal(ChatStatus.Pending, (await store.GetChatMessageAsync("proj-1", msg.Id))!.Status);
    }

    /// Same hazard on the collection inside a doc: ChatTurn.ToolCalls must not alias the stored reply's list,
    /// or an append after the upsert would show up in the fake and be absent in Cosmos — silently truncating
    /// the trail that links a sentence in the chat to the record it changed.
    [Fact]
    public async Task ChatTurn_ToolCalls_DoNotAliasTheStoredReply()
    {
        var store = new InMemoryRecordStore();
        var reply = Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z");
        reply.ToolCalls.Add(new ChatToolCall("search_regulatory", "REACH Annex XVII", null));
        await store.UpsertChatReplyAsync(reply);

        reply.ToolCalls.Add(new ChatToolCall("apply_revision", "Ba tier B → C", "proj-1|revision|discovery|x"));

        var only = Assert.Single(await store.GetChatThreadAsync("proj-1", Stages.Discovery));
        Assert.Single(only.ToolCalls);   // the un-upserted second call was never persisted
    }

    /// Both stores sort through ChatTurns.InOrder, so the ordering contract is tested once, here, against an
    /// input the stores themselves cannot produce: an agent turn ARRIVING FIRST with a timestamp equal to the
    /// operator turn it answers (same clock tick, or a writer that rounds to the second).
    ///
    /// That input is the whole point. Asserting this through the fake would be theatre — the fake enumerates
    /// messages before replies, so a stable sort puts the operator first even with the tiebreak deleted, and
    /// the test could not fail. Handing InOrder the reply first is what makes the tiebreak load-bearing. An
    /// answer printed above its own question is a transcript that lies about who said what first.
    [Fact]
    public void ChatTurns_InOrder_OnAnEqualTimestamp_PutsTheOperatorTurnBeforeTheAgentsReply()
    {
        var ordered = ChatTurns.InOrder([
            new ChatTurn(ChatRoles.Agent,    "reply",  "2026-07-13T01:00:00Z", []),
            new ChatTurn(ChatRoles.Operator, "msg",    "2026-07-13T01:00:00Z", []),
            new ChatTurn(ChatRoles.Operator, "later",  "2026-07-13T02:00:00Z", []),
        ]);

        Assert.Equal(["msg", "reply", "later"], ordered.Select(t => t.Text));
    }
}
