using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Smx.Functions.Sds.Sourcing;

/// Brave, called straight from `regsync` — not through the Search Proxy (spec D4).
///
/// A deliberate near-duplicate of `Smx.SearchProxy.Providers.BraveSearchProvider`. `regsync` must not take
/// a reference on the proxy: the proxy is a separate app with a separate identity and zero corpus RBAC,
/// and its whole job is to be the ONLY public egress for project-revealing queries. Sharing code would
/// couple the two deployables to keep a ~40-line HTTP call DRY.
public sealed class BraveSdsWebSearch(
    HttpClient http, string apiKey, ILogger<BraveSdsWebSearch> log, int maxResponseBytes = 2 * 1024 * 1024)
    : ISdsWebSearch
{
    public const string ApiHost = "api.search.brave.com";
    private const string ApiPath = "/res/v1/web/search";

    public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var qs = $"?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}";
            var target = new Uri(new UriBuilder("https", ApiHost) { Path = ApiPath }.Uri, ApiPath + qs);

            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            req.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // No retry loop here on purpose. Discovery is the fallback leg of a fetch that is already
                // running against a time budget; a search that is down this second buys more by being
                // skipped than by being waited on. The entry is retried on its own backoff anyway.
                log.LogWarning("SDS web search → {Status}; discovery contributes nothing this pass", (int)resp.StatusCode);
                return [];
            }

            // Ceiling before parse: a hostile or broken upstream must not be able to make us buffer
            // its whole response into memory.
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > maxResponseBytes)
            {
                log.LogWarning("SDS web search response oversize ({Len} bytes); ignoring", bytes.Length);
                return [];
            }
            return Parse(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Everything else — DNS, TLS, a reset socket, malformed JSON — becomes "no hits". The caller
            // is a fallback strategy; making it throw would turn a search outage into a failed fetch.
            log.LogWarning(ex, "SDS web search failed; discovery contributes nothing this pass");
            return [];
        }
    }

    private static IReadOnlyList<WebHit> Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<WebHit>();
        foreach (var r in results.EnumerateArray())
        {
            var url = Str(r, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            hits.Add(new WebHit(uri, Str(r, "title") ?? url));
        }
        return hits;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
