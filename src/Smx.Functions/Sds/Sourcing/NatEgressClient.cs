using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class NatEgressClient : IEgressClient
{
    private readonly HttpClient _http;
    private readonly IReadOnlySet<string> _allowlistDomains;
    private readonly SdsOptions _opts;
    private readonly ILogger<NatEgressClient> _log;

    public NatEgressClient(HttpClient http, AllowlistProvider allowlist, SdsOptions opts, ILogger<NatEgressClient> log)
    {
        _http = http;
        _allowlistDomains = allowlist.Domains;
        _opts = opts;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(opts.FetchTimeoutSeconds);
    }

    public async Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct)
    {
        var host = url.Host.ToLowerInvariant();
        if (!_allowlistDomains.Any(d => host == d || host.EndsWith("." + d)))
        {
            _log.LogWarning("Egress blocked: host {Host} not on allowlist", host);
            return null;
        }
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > _opts.MaxPdfBytes) { _log.LogWarning("Egress oversize {Len}", bytes.Length); return null; }
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new EgressResult(bytes, ctype, resp.RequestMessage?.RequestUri ?? url);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Egress fetch failed for {Url}", url); return null; }
    }
}
