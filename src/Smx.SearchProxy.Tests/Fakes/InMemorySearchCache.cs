using System.Globalization;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Tests.Fakes;

public sealed class InMemorySearchCache(int ttlHours) : ISearchCache
{
    private readonly Dictionary<string, (string FetchedAt, IReadOnlyList<SearchHit> Hits)> _store = [];
    public int Writes { get; private set; }

    public Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var e)) return Task.FromResult<IReadOnlyList<SearchHit>?>(null);
        var fetchedAt = DateTimeOffset.Parse(e.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var fresh = now - fetchedAt < TimeSpan.FromHours(ttlHours);
        return Task.FromResult(fresh ? e.Hits : null);
    }

    public Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct)
    {
        _store[key] = (nowUtc, hits);
        Writes++;
        return Task.CompletedTask;
    }
}
