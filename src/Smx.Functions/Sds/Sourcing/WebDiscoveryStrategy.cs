using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

/// The strategy for substances no curated template covers — which, on 2026-07-29, was 40 of 53.
///
/// It is an `ISourceStrategy` like the others, but it has no allowlist row and never will: it is not
/// keyed to a supplier. `SourceResolver` therefore runs it once after the curated walk rather than per
/// entry, and only when that walk came back empty.
public sealed class WebDiscoveryStrategy(ISdsWebSearch search, int maxCandidates = 5) : ISourceStrategy
{
    public string Name => "webDiscovery";

    /// The row this strategy would have had, if it were about a supplier. `SourceResolver` passes it so
    /// the `ISourceStrategy` signature holds; nothing here reads it.
    public static readonly AllowlistEntry NoSupplier =
        new("", "", int.MaxValue, "webDiscovery", "", null, null);

    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        // Chemistry only. There is no field here a project identity could travel in, and that is the
        // point: `ensure` is keyed by substance, never by project. `filetype:pdf` biases what comes back;
        // the ordering below still re-sorts it, because a URL with no `.pdf` extension can still serve
        // one and is worth keeping as a lower-ranked candidate.
        var query = $"\"{key.Cas}\" {key.Element} {key.Form} safety data sheet SDS filetype:pdf";

        // Ask for more than we will use: the ranking below only has something to do if the response
        // contains both kinds of URL.
        var hits = await search.SearchAsync(query, maxCandidates * 2, ct);

        return hits
            .OrderByDescending(h => h.Url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Take(maxCandidates)
            // Supplier and domain are both the host: nobody curated a display name for it, and inventing
            // one would put a fabricated supplier on the registry record.
            .Select(h => new SourceCandidate(h.Url.Host, h.Url.Host, h.Url, "webDiscovery"))
            .ToList();
    }
}
