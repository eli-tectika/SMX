using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class SourceResolver
{
    private readonly AllowlistProvider _allowlist;
    private readonly IReadOnlyDictionary<string, ISourceStrategy> _strategies;
    private readonly WebDiscoveryStrategy? _webDiscovery;

    // `webDiscovery` is optional because a dry run and a keyless local host have nothing to search with,
    // and the curated walk must still work without it.
    public SourceResolver(AllowlistProvider allowlist, IEnumerable<ISourceStrategy> strategies,
        WebDiscoveryStrategy? webDiscovery = null)
    {
        _allowlist = allowlist;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _webDiscovery = webDiscovery;
    }

    // Walks the ordered allowlist and yields candidates per entry. productLookup entries may
    // egress via `fetch` here; the SDS PDF fetch itself happens in the sweep.
    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var candidates = new List<SourceCandidate>();
        foreach (var entry in _allowlist.Ordered)
        {
            if (!_strategies.TryGetValue(entry.Strategy, out var strat)) continue;
            candidates.AddRange(await strat.ResolveAsync(entry, key, fetch, ct));
        }

        // Web discovery is a FALLBACK, not an addition. It has no allowlist row, so the walk above can
        // never reach it — and it runs only when that walk came back empty, so the curated fast path
        // stays deterministic and never pays for a metered search call it does not need.
        if (candidates.Count == 0 && _webDiscovery is not null)
            candidates.AddRange(
                await _webDiscovery.ResolveAsync(WebDiscoveryStrategy.NoSupplier, key, fetch, ct));

        return candidates;
    }
}
