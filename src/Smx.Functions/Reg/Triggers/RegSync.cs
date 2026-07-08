using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Ingestion;

namespace Smx.Functions.Reg.Triggers;

// Monthly Regulatory Sync — the SDS-style plain TimerTrigger (no Durable). CRON is app-setting-driven
// (REG_SYNC_CRON, default "0 0 3 1 * *" = 03:00 on the 1st). The testable core lives on SyncPipeline.
public sealed class RegSync
{
    private readonly SyncPipeline _pipeline;
    private readonly ILogger<RegSync> _log;

    public RegSync(SyncPipeline pipeline, ILogger<RegSync> log) { _pipeline = pipeline; _log = log; }

    [Function("RegSync")]
    public async Task Run([TimerTrigger("%REG_SYNC_CRON%")] TimerInfo timer, CancellationToken ct)
    {
        _log.LogInformation("RegSync timer fired");
        await _pipeline.RunSyncAsync(DateTimeOffset.UtcNow.ToString("O"), ct);
    }
}
