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

    [Fact]
    public async Task Due_selects_pending_failed_under_cap_and_stale_fetched()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        await store.UpsertAsync(new MasterListEntry("a_x","a","x","1",null,SdsStatus.Pending,"sweep","t",null,0), default);
        await store.UpsertAsync(new MasterListEntry("b_x","b","x","1",null,SdsStatus.Failed,"sweep","t","t",2), default);
        await store.UpsertAsync(new MasterListEntry("c_x","c","x","1",null,SdsStatus.Failed,"sweep","t","t",9), default);
        await store.UpsertAsync(new MasterListEntry("d_x","d","x","1",null,SdsStatus.AwaitingOperator,"sweep","t","t",3), default);
        await store.UpsertAsync(new MasterListEntry("e_x","e","x","1",null,SdsStatus.Fetched,"sweep","t","2000-01-01T00:00:00Z",1), default);

        var due = await repo.QueryDueAsync(retryCap: 3, recheckDays: 90, nowUtc: "2026-07-07T00:00:00Z", default);
        var ids = due.Select(x => x.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a_x", "b_x", "e_x" }, ids); // pending, failed<cap, stale-fetched; NOT failed>=cap, NOT awaiting
    }
}
