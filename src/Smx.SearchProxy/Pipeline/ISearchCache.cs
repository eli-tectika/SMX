using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// `nowUtc` is passed in (never DateTime.UtcNow inside) so TTL behaviour is deterministic in tests — the
/// same convention as SdsSweep.RunSweepAsync / SyncPipeline.RunSyncAsync.
public interface ISearchCache
{
    Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct);
    Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct);
}
