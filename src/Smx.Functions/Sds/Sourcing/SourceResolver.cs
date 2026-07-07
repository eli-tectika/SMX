using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class SourceResolver
{
    private readonly AllowlistProvider _allowlist;
    private readonly IReadOnlyDictionary<string, ISourceStrategy> _strategies;

    public SourceResolver(AllowlistProvider allowlist, IEnumerable<ISourceStrategy> strategies)
    {
        _allowlist = allowlist;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
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
        return candidates;
    }
}
