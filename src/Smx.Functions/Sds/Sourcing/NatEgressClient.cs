using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class NatEgressClient : IEgressClient
{
    private readonly HttpClient _http;
    private readonly SdsOptions _opts;
    private readonly ILogger<NatEgressClient> _log;

    public NatEgressClient(HttpClient http, SdsOptions opts, ILogger<NatEgressClient> log)
    {
        _http = http;
        _opts = opts;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(opts.FetchTimeoutSeconds);
    }

    /// The allowlist gate is gone (2026-07-29 design). What remains below is robustness, not policy:
    /// each rail refuses BEFORE the request is made, and the default for an unknown host is now allow.
    /// Whether a fetched document is the right document is SdsValidator's question, not this class's.
    public async Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            _log.LogWarning("Egress refused: {Url} is not https", url);
            return null;
        }

        var host = url.Host.ToLowerInvariant();
        // Suffix match on a label boundary — "tarpit.example" must cover "cdn.tarpit.example" without
        // also swallowing "nottarpit.example".
        if (_opts.Denylist.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)))
        {
            _log.LogWarning("Egress refused: host {Host} is denylisted", host);
            return null;
        }
        try
        {
            // The fetch timeout must bound the WHOLE fetch. With ResponseHeadersRead,
            // HttpClient.Timeout stops covering the body read — a tarpit server that sent headers
            // and then trickled the body hung the live sweep for 40+ minutes (2026-07-16).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_opts.FetchTimeoutSeconds));

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            // Cap the size DURING the read — buffering an unbounded body before checking would
            // let a hostile server exhaust memory long before the length check.
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > _opts.MaxPdfBytes) { _log.LogWarning("Egress oversize (> {Max} bytes)", _opts.MaxPdfBytes); return null; }
            }
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new EgressResult(buffer.ToArray(), ctype, resp.RequestMessage?.RequestUri ?? url);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Egress fetch failed for {Url}", url); return null; }
    }
}
