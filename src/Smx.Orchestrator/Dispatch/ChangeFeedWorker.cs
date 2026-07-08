using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Dispatch;

public sealed class ChangeFeedWorker(
    CosmosClient cosmos, BackendOptions opts, StageDispatcher dispatcher,
    ILogger<ChangeFeedWorker> logger) : BackgroundService
{
    private ChangeFeedProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = cosmos.GetDatabase(opts.CosmosDatabase);
        var monitored = db.GetContainer(opts.RecordContainer);
        var leases = db.GetContainer(opts.LeaseContainer);

        _processor = monitored
            .GetChangeFeedProcessorBuilder<JsonElement>("smx-orchestrator", HandleChangesAsync)
            .WithInstanceName(Environment.MachineName)
            .WithLeaseContainer(leases)
            .WithStartTime(DateTime.MinValue.ToUniversalTime()) // process history on first start
            .Build();

        await _processor.StartAsync();
        logger.LogInformation("change feed processor started on {Container}", opts.RecordContainer);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
        await _processor.StopAsync();
    }

    private async Task HandleChangesAsync(
        ChangeFeedProcessorContext context, IReadOnlyCollection<JsonElement> changes, CancellationToken ct)
    {
        foreach (var change in changes)
        {
            var doc = RecordDocRouter.Route(change);
            if (doc is null) continue;
            try
            {
                await dispatcher.OnRecordChangedAsync(doc, ct);
            }
            catch (Exception e)
            {
                logger.LogError(e, "dispatch failed for record change {Id}",
                    change.TryGetProperty("id", out var id) ? id.GetString() : "?");
            }
        }
    }
}
