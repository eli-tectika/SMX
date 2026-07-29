using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// Curated CAS -> product-number map carried in the allowlist entry itself (git-versioned,
// PR-reviewed — the same curation philosophy as the search-proxy cover corpus). Exists because
// the 2026-07-16 shakedown found the only live-fetchable SDS source (fishersci.com's msds
// endpoint) needs a product number that no fetchable search can supply. Resolves with ZERO
// egress: an unmapped CAS simply yields no candidate and the entry parks for the operator.
public sealed class StaticMapStrategy : ISourceStrategy
{
    public string Name => "staticMap";

    public Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        IReadOnlyList<SourceCandidate> result = Array.Empty<SourceCandidate>();
        if (entry.CasMap is not null && entry.CasMap.TryGetValue(key.Cas, out var productNumber))
        {
            var url = entry.SdsUrlTemplate
                .Replace("{productNumber}", productNumber)
                .Replace("{cas}", key.Cas);
            result = new[] { new SourceCandidate(entry.Supplier, entry.Domain, new Uri(url), "staticMap") };
        }
        return Task.FromResult(result);
    }
}
