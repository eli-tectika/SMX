using Smx.SearchProxy.Config;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Tests.Fakes;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class QuotaGuardTests
{
    private static QuotaGuard Guard(int monthlyCap, int perMinute, InMemoryQuotaStore store) =>
        new(store, new ProxyOptions { MonthlyQueryCap = monthlyCap, RateLimitPerMinute = perMinute });

    [Fact]
    public async Task AllowsUntilTheMonthlyCap_ThenRefuses()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 10, perMinute: 1000, store);

        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:01:00Z", default));
        // 8 spent; a 4-query batch would reach 12 > 10.
        Assert.False(await guard.TryConsumeAsync(4, "2026-07-13T10:02:00Z", default));
    }

    // The cap is on PROVIDER CALLS, decoys included — that is what Brave bills for.
    [Fact]
    public async Task TheCapCountsDecoysNotJustRealQueries()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 4, perMinute: 1000, store);
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.Equal(4, store.CountFor("2026-07"));
        Assert.False(await guard.TryConsumeAsync(1, "2026-07-13T10:00:01Z", default));
    }

    [Fact]
    public async Task TheCapResetsWithTheMonth()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 4, perMinute: 1000, store);
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-31T23:59:00Z", default));
        Assert.True(await guard.TryConsumeAsync(4, "2026-08-01T00:00:00Z", default));
    }

    [Fact]
    public async Task RateLimit_RefusesABurstWithinTheSameMinute()
    {
        var guard = Guard(monthlyCap: 10_000, perMinute: 5, new InMemoryQuotaStore());
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.False(await guard.TryConsumeAsync(4, "2026-07-13T10:00:30Z", default)); // 8 > 5 in the minute
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:01:00Z", default));  // new minute
    }
}
