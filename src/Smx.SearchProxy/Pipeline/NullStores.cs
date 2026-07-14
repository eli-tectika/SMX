using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// Dry-run has no storage account to talk to. A cache that never hits and a quota that never binds keep the
/// pipeline's shape identical to production — the dry run exercises the real code path, not a shortcut.
public sealed class NullSearchCache : ISearchCache
{
    public Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>?>(null);

    public Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class NullQuotaStore : IQuotaStore
{
    public Task<int> ReadAsync(string month, CancellationToken ct) => Task.FromResult(0);
    public Task<int> AddAsync(string month, int delta, CancellationToken ct) => Task.FromResult(delta);
}
