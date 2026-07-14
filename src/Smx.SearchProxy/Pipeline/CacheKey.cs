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
    public static string For(string query, string intent, int maxResults)
    {
        var normalized = Whitespace().Replace(query.Trim().ToLowerInvariant(), " ");
        var material = $"{intent}|{maxResults}|{normalized}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
