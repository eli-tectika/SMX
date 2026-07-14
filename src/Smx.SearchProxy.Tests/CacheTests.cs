using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Tests.Fakes;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class CacheTests
{
    private static readonly IReadOnlyList<SearchHit> Hits =
        [new SearchHit("t", "https://example.org/a", "s", "example.org", null)];

    [Fact]
    public void Key_IsStableUnderWhitespaceAndCase()
    {
        Assert.Equal(
            CacheKey.For("Yttrium   Neodecanoate ", SearchIntents.CandidateForms, 10),
            CacheKey.For("yttrium neodecanoate", SearchIntents.CandidateForms, 10));
    }

    [Fact]
    public void Key_DiffersByIntentAndMaxResults()
    {
        var a = CacheKey.For("q", SearchIntents.CandidateForms, 10);
        Assert.NotEqual(a, CacheKey.For("q", SearchIntents.FormProperties, 10));
        Assert.NotEqual(a, CacheKey.For("q", SearchIntents.CandidateForms, 5));
    }

    [Fact]
    public async Task RoundTripsWithinTtl()
    {
        var cache = new InMemorySearchCache(ttlHours: 168);
        await cache.SetAsync("k", Hits, "2026-07-13T10:00:00Z", default);
        var got = await cache.GetAsync("k", "2026-07-14T10:00:00Z", default);
        Assert.NotNull(got);
        Assert.Equal("https://example.org/a", got![0].Url);
    }

    [Fact]
    public async Task ExpiredEntry_IsAMiss()
    {
        var cache = new InMemorySearchCache(ttlHours: 168);
        await cache.SetAsync("k", Hits, "2026-07-01T10:00:00Z", default);
        Assert.Null(await cache.GetAsync("k", "2026-07-13T10:00:00Z", default)); // 12 days > 7
    }

    [Fact]
    public async Task UnknownKey_IsAMiss() =>
        Assert.Null(await new InMemorySearchCache(168).GetAsync("nope", "2026-07-13T10:00:00Z", default));
}
