using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Smx.SearchProxy.Pipeline;

public static partial class CacheKey
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// Content-addressed. The key is a hash, not the query text — so the cache blob names in storage do not
    /// themselves become a readable log of what we searched for.
    ///
    /// `freshnessDays` is part of the material: it scopes the results (a 7-day window is a different answer
    /// than a 30-day one), so two requests differing only in freshness must NOT share a cache entry. Null
    /// (no freshness filter) hashes as the fixed token "-", distinct from any numeric window.
    public static string For(string query, string intent, int maxResults, int? freshnessDays = null)
    {
        var normalized = Whitespace().Replace(query.Trim().ToLowerInvariant(), " ");
        var freshness = freshnessDays?.ToString() ?? "-";
        var material = $"{intent}|{maxResults}|{freshness}|{normalized}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
