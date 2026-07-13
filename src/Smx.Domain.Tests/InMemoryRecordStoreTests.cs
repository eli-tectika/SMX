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
        // The fake filters by CLR type (.OfType<ChatMessageDoc>()) while Cosmos filters by the `type` string
        // field. That is the one place the twins use different mechanisms, so it is the one place they can
        // silently diverge.
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
}
