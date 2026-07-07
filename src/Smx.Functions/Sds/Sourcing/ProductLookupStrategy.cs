using System.Text;
using System.Text.RegularExpressions;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// Deterministic two-step: GET the supplier search page for the CAS (via the supplied egress fetch),
// regex out (brand, productNumber), then build the SDS PDF URL. Supplier-specific templates/regex
// live in the allowlist; new suppliers are a data edit.
public sealed class ProductLookupStrategy : ISourceStrategy
{
    public string Name => "productLookup";

    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var empty = Array.Empty<SourceCandidate>();
        if (string.IsNullOrEmpty(entry.SearchUrlTemplate) || string.IsNullOrEmpty(entry.ProductNumberRegex))
            return empty;

        var searchUrl = new Uri(entry.SearchUrlTemplate.Replace("{cas}", key.Cas));
        var page = await fetch(searchUrl, ct);
        if (page is null) return empty;

        var html = Encoding.UTF8.GetString(page.Content);
        var m = Regex.Match(html, entry.ProductNumberRegex, RegexOptions.IgnoreCase);
        if (!m.Success) return empty;

        var url = entry.SdsUrlTemplate
            .Replace("{brand}", m.Groups["brand"].Value)
            .Replace("{productNumber}", m.Groups["productNumber"].Value);
        return new[] { new SourceCandidate(entry.Supplier, entry.Domain, new Uri(url)) };
    }
}
