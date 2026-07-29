using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Triggers;
using Xunit;

public class MigrateParkedEntriesTests
{
    private static MasterListEntry Parked(string element) => new(
        DedupKey.ForMasterList(element, "TMHD complex"), element, "TMHD complex", "1-1-1", null,
        ParkedEntryMigration.DeletedStatus, "operator", "2026-07-01T00:00:00Z", "2026-07-27T03:00:00Z", 3);

    [Fact]
    public async Task Parked_entries_become_failed_and_immediately_due()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);
        await store.UpsertAsync(Parked("Ce"), default);
        var repo = new MasterListRepo(store);

        var moved = await ParkedEntryMigration.RunAsync(store, "2026-07-29T00:00:00Z", default);

        Assert.Equal(2, moved);
        Assert.Equal(2, (await repo.QueryDueAsync(90, "2026-07-29T00:00:01Z", default)).Count);
    }

    // AttemptCount is the record of what was already tried. Resetting it would erase the evidence that
    // these suppliers are hard, and the next failure would restart the backoff at one day.
    [Fact]
    public async Task Attempt_history_is_preserved_not_reset()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);

        await ParkedEntryMigration.RunAsync(store, "2026-07-29T00:00:00Z", default);

        var e = (await new MasterListRepo(store).GetAsync("Zr", "TMHD complex", default))!;
        Assert.Equal(SdsStatus.Failed, e.Status);
        Assert.Equal(3, e.AttemptCount);
    }

    [Fact]
    public async Task Running_twice_moves_nothing_the_second_time()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);

        Assert.Equal(1, await ParkedEntryMigration.RunAsync(store, "2026-07-29T00:00:00Z", default));
        Assert.Equal(0, await ParkedEntryMigration.RunAsync(store, "2026-07-29T00:00:00Z", default));
    }

    // The migration must be surgical: it selects on the dead status alone and must not disturb a row
    // that is legitimately waiting out a backoff.
    [Fact]
    public async Task A_healthy_failed_entry_is_left_alone()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(new MasterListEntry(
            "keep_x", "keep", "x", "1-1-1", null, SdsStatus.Failed, "sweep",
            "2026-07-01T00:00:00Z", "2026-07-27T00:00:00Z", 2, "2026-08-15T00:00:00Z"), default);

        Assert.Equal(0, await ParkedEntryMigration.RunAsync(store, "2026-07-29T00:00:00Z", default));

        var e = (await store.GetAsync("keep_x", "keep", default))!;
        Assert.Equal("2026-08-15T00:00:00Z", e.NextAttemptUtc);
    }
}
