using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Xunit;

public class MasterListRepoTests
{
    [Fact]
    public async Task Append_is_idempotent_no_duplicate()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        var first = await repo.AppendAsync("Yb", "neodecanoate", "27253-31-2", null, "agent", "2026-07-07T00:00:00Z", default);
        var second = await repo.AppendAsync("Yb", "Neodecanoate", "27253-31-2", null, "agent", "2026-07-07T00:00:00Z", default);
        Assert.True(first);
        Assert.False(second);
        Assert.Single(store.Items);
        Assert.Equal(SdsStatus.Pending, store.Items.Values.Single().Status);
    }

    // Was `Due_selects_pending_failed_under_cap_and_stale_fetched`. There is no cap any more, so the
    // selection rule is now "pending, failed whose backoff has elapsed, and stale fetched".
    [Fact]
    public async Task Due_selects_pending_elapsed_failed_and_stale_fetched()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        await store.UpsertAsync(new MasterListEntry("a_x","a","x","1",null,SdsStatus.Pending,"sweep","t",null,0), default);
        // Failed with an elapsed backoff -> due, regardless of how many attempts it has behind it.
        await store.UpsertAsync(new MasterListEntry("b_x","b","x","1",null,SdsStatus.Failed,"sweep","t","t",2,"2026-07-01T00:00:00Z"), default);
        await store.UpsertAsync(new MasterListEntry("c_x","c","x","1",null,SdsStatus.Failed,"sweep","t","t",9,"2026-07-06T23:59:59Z"), default);
        // Failed but not yet elapsed -> not due YET. This is a wait, not an ending.
        await store.UpsertAsync(new MasterListEntry("d_x","d","x","1",null,SdsStatus.Failed,"sweep","t","t",3,"2026-08-01T00:00:00Z"), default);
        await store.UpsertAsync(new MasterListEntry("e_x","e","x","1",null,SdsStatus.Fetched,"sweep","t","2000-01-01T00:00:00Z",1), default);

        var due = await repo.QueryDueAsync(recheckDays: 90, nowUtc: "2026-07-07T00:00:00Z", default);

        Assert.Equal(new[] { "a_x", "b_x", "c_x", "e_x" }, due.Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    // A failed entry written before NextAttemptUtc existed has none. It must be treated as due rather
    // than as parked forever — the null case is exactly the 40 rows this change exists to rescue.
    [Fact]
    public async Task A_failed_entry_with_no_next_attempt_stamp_is_due()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        await store.UpsertAsync(new MasterListEntry("legacy_x","legacy","x","1",null,SdsStatus.Failed,"sweep","t","t",7), default);

        Assert.Single(await repo.QueryDueAsync(recheckDays: 90, nowUtc: "2026-07-07T00:00:00Z", default));
    }

    [Fact]
    public async Task A_failed_entry_is_not_due_until_its_backoff_elapses()
    {
        var repo = new MasterListRepo(new InMemoryMasterListStore());
        await repo.AppendAsync("Zr", "TMHD complex", "18865-74-2", null, "op", "2026-07-01T00:00:00Z", default);
        var entry = (await repo.GetAsync("Zr", "TMHD complex", default))!;

        await repo.RecordFailureAsync(entry, "2026-07-01T00:00:00Z", default);   // attempt 1 -> +1 day

        Assert.Empty(await repo.QueryDueAsync(90, "2026-07-01T12:00:00Z", default));
        Assert.Single(await repo.QueryDueAsync(90, "2026-07-02T00:00:00Z", default));
    }

    // THE regression this change exists to prevent. No number of failures may ever remove an entry from
    // the due set permanently — on 2026-07-29 three failures did exactly that to 40 of 53 substances.
    [Fact]
    public async Task No_number_of_failures_makes_an_entry_permanently_undue()
    {
        var repo = new MasterListRepo(new InMemoryMasterListStore());
        await repo.AppendAsync("Y", "Fluoride", "13709-49-4", null, "op", "2026-01-01T00:00:00Z", default);

        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var i = 0; i < 25; i++)
        {
            var entry = (await repo.GetAsync("Y", "Fluoride", default))!;
            await repo.RecordFailureAsync(entry, now.ToString("O"), default);
            now = now.AddDays(BackoffSchedule.MaxDelayDays + 1);
            Assert.Single(await repo.QueryDueAsync(90, now.ToString("O"), default));
        }

        var final = (await repo.GetAsync("Y", "Fluoride", default))!;
        Assert.Equal(SdsStatus.Failed, final.Status);
        Assert.Equal(25, final.AttemptCount);   // the history of what was tried survives
    }

    // A success has to clear the backoff stamp, or the revision recheck 90 days later would be shadowed
    // by a stale NextAttemptUtc left over from the failures that preceded it.
    [Fact]
    public async Task Marking_fetched_clears_the_backoff_stamp()
    {
        var repo = new MasterListRepo(new InMemoryMasterListStore());
        await repo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "op", "2026-01-01T00:00:00Z", default);
        var entry = (await repo.GetAsync("Na", "hydroxide", default))!;
        await repo.RecordFailureAsync(entry, "2026-01-01T00:00:00Z", default);

        var failed = (await repo.GetAsync("Na", "hydroxide", default))!;
        Assert.NotNull(failed.NextAttemptUtc);

        await repo.MarkFetchedAsync(failed, "2026-01-05T00:00:00Z", default);

        var fetched = (await repo.GetAsync("Na", "hydroxide", default))!;
        Assert.Equal(SdsStatus.Fetched, fetched.Status);
        Assert.Null(fetched.NextAttemptUtc);
    }
}
