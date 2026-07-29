using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Smx.Functions.Sds.Triggers;

public sealed record SyncRequest(int? MaxEntries, int? MaxDurationSeconds);

/// "Run the sync now" — the operator's version of waiting for tomorrow's timer.
///
/// Bounded, and re-runnable until the report says nothing is outstanding. The bounds are not caution for
/// its own sake: the 2026-07-16 full sweep took 27 minutes against a 30-minute host timeout, so an
/// unbounded manual run is a gamble against the platform's clock with no partial credit.
public sealed class RunSdsSync
{
    private const int DefaultMaxEntries = 100;
    private const int DefaultMaxDurationSeconds = 600;

    private readonly SdsSweep _sweep;
    public RunSdsSync(SdsSweep sweep) => _sweep = sweep;

    [Function("RunSdsSync")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/sync")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;

        SyncRequest? body = null;
        try
        {
            if (req.Body.CanSeek && req.Body.Length > 0 || !req.Body.CanSeek)
                body = await JsonSerializer.DeserializeAsync<SyncRequest>(
                    req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException) { /* an empty or malformed body just means "use the defaults" */ }

        var maxEntries = body?.MaxEntries is > 0 ? body.MaxEntries.Value : DefaultMaxEntries;
        var maxSeconds = body?.MaxDurationSeconds is > 0 ? body.MaxDurationSeconds.Value : DefaultMaxDurationSeconds;

        var report = await _sweep.RunSweepAsync(
            DateTimeOffset.UtcNow.ToString("O"), maxEntries, TimeSpan.FromSeconds(maxSeconds), ct);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(report, ct);
        return resp;
    }
}
