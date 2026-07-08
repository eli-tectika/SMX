using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Config;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Reg.Sourcing;

// NAT-egress fetcher for the regulatory corpus. Same shape as Sds/Sourcing/NatEgressClient, but scoped to the
// regulator allowlist and RegOptions size/timeout bounds (regulatory datasets are larger than a single SDS PDF).
public sealed class RegNatEgressClient : IRegEgress
{
    private readonly HttpClient _http;
    private readonly IReadOnlySet<string> _allowlistDomains;
    private readonly RegOptions _opts;
    private readonly ILogger<RegNatEgressClient> _log;

    public RegNatEgressClient(HttpClient http, RegRegistryProvider registry, RegOptions opts, ILogger<RegNatEgressClient> log)
    {
        _http = http;
        _allowlistDomains = registry.Domains;
        _opts = opts;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(opts.FetchTimeoutSeconds);
    }

    public async Task<EgressResult?> FetchAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var host = url.Host.ToLowerInvariant();
        if (!_allowlistDomains.Any(d => host == d || host.EndsWith("." + d)))
        {
            _log.LogWarning("Reg egress blocked: host {Host} not on the regulator allowlist", host);
            return null;
        }
        var attempts = Math.Max(1, _opts.EgressRetries);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                // Per-request headers (not on the shared HttpClient, which is reused across sources).
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                    foreach (var (k, v) in headers)
                        req.Headers.TryAddWithoutValidation(k, v);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                // Retry only transient server-side failures; 4xx is a permanent registry/URL error — don't retry.
                if ((int)resp.StatusCode >= 500 && attempt < attempts)
                {
                    _log.LogWarning("Reg egress {Url} → {Status}, retry {Attempt}/{Max}", url, (int)resp.StatusCode, attempt, attempts);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) { _log.LogWarning("Reg egress {Url} → {Status}", url, (int)resp.StatusCode); return null; }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > _opts.MaxDocBytes) { _log.LogWarning("Reg egress oversize {Len}", bytes.Length); return null; }
                var ctype = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                return new EgressResult(bytes, ctype, resp.RequestMessage?.RequestUri ?? url);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < attempts)
            {
                _log.LogWarning(ex, "Reg egress attempt {Attempt}/{Max} failed for {Url}", attempt, attempts, url);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Reg egress fetch failed for {Url}", url); return null; }
        }
        return null;
    }
}
