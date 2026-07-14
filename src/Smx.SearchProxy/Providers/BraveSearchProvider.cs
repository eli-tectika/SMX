using System.Text.Json;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// The one component in this system that talks to the public internet at agent time.
///
/// Bing Search v7 was retired 2025-08-11 (410 Gone) and its replacement, Grounding with Bing, requires the
/// Foundry Agent Service this project cut. Brave runs its own index, so we are not proxying Google or Bing,
/// and its privacy positioning matches the claim we make to the client.
public sealed class BraveSearchProvider(HttpClient http, ProxyOptions opts, ILogger<BraveSearchProvider> log) : ISearchProvider
{
    /// Invariant 1 (spec §3): exactly one upstream host, ever. An allowlist of one.
    public const string ApiHost = "api.search.brave.com";
    private const string ApiPath = "/res/v1/web/search";

    public async Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct)
    {
        var url = new UriBuilder("https", ApiHost) { Path = ApiPath }.Uri;
        var qs = $"?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}";
        if (freshnessDays is > 0) qs += $"&freshness=pd{freshnessDays}";
        var target = new Uri(url, ApiPath + qs);

        if (target.Host != ApiHost)
        {
            log.LogError("Provider egress blocked: host {Host} is not the single allowed upstream", target.Host);
            return null;
        }

        var attempts = Math.Max(1, opts.Retries);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                // A FRESH request each attempt, carrying ONLY what Brave needs. No cookies, no referrer, no
                // caller identity. The W3C traceparent header that HttpClient would otherwise inject is
                // suppressed at the handler (see Program.cs) — it would be a correlation handle handed to
                // Brave for free, and it would defeat the cover batch by grouping the N queries as one trace.
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                req.Headers.TryAddWithoutValidation("X-Subscription-Token", opts.ApiKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                var transient = (int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                if (transient && attempt < attempts)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                    log.LogWarning("Brave → {Status}, retry {Attempt}/{Max} after {Wait}s", (int)resp.StatusCode, attempt, attempts, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    // 4xx is permanent: a bad key or a malformed query will not heal on retry, it only burns quota.
                    log.LogWarning("Brave → {Status}; giving up", (int)resp.StatusCode);
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > opts.MaxResponseBytes)
                {
                    log.LogWarning("Brave response oversize ({Len} bytes)", bytes.Length);
                    return null;
                }
                return Parse(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < attempts)
            {
                log.LogWarning(ex, "Brave attempt {Attempt}/{Max} failed", attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Brave search failed");
                return null;
            }
        }
        return null;
    }

    private static IReadOnlyList<SearchHit> Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<SearchHit>();
        foreach (var r in results.EnumerateArray())
        {
            var url = Str(r, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var host = r.TryGetProperty("meta_url", out var meta) ? Str(meta, "hostname") : null;
            hits.Add(new SearchHit(
                Title: Str(r, "title") ?? url,
                Url: url,
                Snippet: Str(r, "description") ?? "",
                Host: host ?? SafeHost(url),
                Age: Str(r, "age")));
        }
        return hits;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}
