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
