using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Acquisition;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

/// What one sweep pass did. `Remaining` is what the bounds left behind — reported rather than silently
/// dropped, so an operator running a manual sync knows whether to run it again.
public sealed record SweepReport(int Examined, int Fetched, int Unavailable, int Remaining);

public sealed class SdsSweep
{
    private readonly MasterListRepo _masterList;
    private readonly SdsAcquirer _acquirer;
    private readonly SdsOptions _opts;
    private readonly ILogger<SdsSweep> _log;

    public SdsSweep(MasterListRepo masterList, SdsAcquirer acquirer, SdsOptions opts, ILogger<SdsSweep> log)
    { _masterList = masterList; _acquirer = acquirer; _opts = opts; _log = log; }

    [Function("SdsSweep")]
    public Task Run([TimerTrigger("%SDS_SWEEP_CRON%")] TimerInfo timer, CancellationToken ct)
        => RunSweepAsync(DateTimeOffset.UtcNow.ToString("O"), ct);

    // Testable core (no trigger attribute): process the whole due set in bulk.
    public Task<SweepReport> RunSweepAsync(string nowUtc, CancellationToken ct)
        => RunSweepAsync(nowUtc, int.MaxValue, Timeout.InfiniteTimeSpan, ct);

    /// Bounded pass. The bounds exist because the 2026-07-16 full sweep took 27 minutes against a
    /// 30-minute host timeout; an operator-triggered sync has to be able to say "that is enough for now"
    /// and come back, rather than gamble the whole run against the platform's clock.
    public async Task<SweepReport> RunSweepAsync(
        string nowUtc, int maxEntries, TimeSpan maxDuration, CancellationToken ct)
    {
        var all = await _masterList.QueryDueAsync(_opts.RevisionRecheckDays, nowUtc, ct);
        var due = all.Take(Math.Max(0, maxEntries)).ToList();
        var remaining = all.Count - due.Count;
        _log.LogInformation("SDS sweep: {Count} due entries ({Remaining} deferred)", due.Count, remaining);

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxDuration != Timeout.InfiniteTimeSpan) bound.CancelAfter(maxDuration);
        var boundedCt = bound.Token;

        var fetched = 0; var examined = 0;

        // Bounded parallelism. The 2026-07-16 sweep took 27 minutes against a 30-minute platform
        // timeout because this was a serial loop behind 30-second fetch timeouts — three minutes from
        // being killed mid-run, with the tail of the batch never attempted. The bound matters as much
        // as the parallelism: an unbounded fan-out would open one socket per due entry and arrive at
        // every supplier as a burst.
        var gate = new SemaphoreSlim(Math.Max(1, _opts.SweepConcurrency));
        await Task.WhenAll(due.Select(async entry =>
        {
            // The duration bound stops us STARTING new work; entries already in flight are allowed to
            // finish, so a bounded run never leaves a half-ingested sheet behind.
            if (boundedCt.IsCancellationRequested) return;
            await gate.WaitAsync(ct);
            try
            {
                Interlocked.Increment(ref examined);
                if (await ProcessEntryAsync(entry, nowUtc, boundedCt, ct)) Interlocked.Increment(ref fetched);
            }
            finally { gate.Release(); }
        }));

        // Entries the duration bound skipped are still outstanding, so they count as remaining too.
        return new SweepReport(examined, fetched, examined - fetched, remaining + (due.Count - examined));
    }

    // One entry, start to finish — delegated to SdsAcquirer so the timer, the operator's manual sync and
    // an agent's ensure_sds all run the SAME code. The catch is what keeps one bad entry from costing the
    // rest of the batch; the acquirer already isolates per-candidate failures and records the outcome, so
    // reaching here means something unexpected went wrong with the entry itself.
    //
    // force: true is not optional. QueryDueAsync has ALREADY decided this entry is due, and a `fetched`
    // row coming up for its revision recheck would otherwise short-circuit on the very sheet the recheck
    // exists to replace.
    private async Task<bool> ProcessEntryAsync(
        MasterListEntry entry, string nowUtc, CancellationToken boundedCt, CancellationToken callerCt)
    {
        try
        {
            var key = new SubstanceKey(entry.Element, entry.Form, entry.Cas);
            var result = await _acquirer.EnsureAsync(key, force: true, nowUtc, boundedCt);
            if (result.Status != EnsureStatus.Fetched)
                _log.LogInformation("Sweep: {Id} unavailable — {Reason}", entry.Id, result.Reason);
            return result.Status == EnsureStatus.Fetched;
        }
        // Only the CALLER cancelling aborts the sweep. The duration bound expiring is an ordinary end to
        // this entry's attempt, and the batch keeps its result.
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Sweep entry {Id} stopped by the run's time bound", entry.Id);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sweep entry {Id} threw; recording failure and continuing", entry.Id);
            await _masterList.RecordFailureAsync(entry, nowUtc, CancellationToken.None);
            return false;
        }
    }
}
