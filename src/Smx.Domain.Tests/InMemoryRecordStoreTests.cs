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
