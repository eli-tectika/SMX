using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// Owns the running pipelines: one task per project, and the registry every control endpoint resolves
/// against.
///
/// A hosted service so <see cref="ResumeAllAsync"/> runs at boot. That resume is not a nicety: without it a
/// process that dies mid-run leaves the project at `running` with nothing to restart it — the exact
/// checkpoint-and-lose failure the change-feed model had, and the one thing that model was supposed to
/// prevent. Before this type existed, such a project stalled silently and permanently.
///
/// REGISTERED AS A SINGLETON, AND THE HOSTED SERVICE MUST RESOLVE THAT SAME SINGLETON
/// (`AddHostedService(sp => sp.GetRequiredService&lt;PipelineSupervisor&gt;())`). A second instance would keep
/// its own empty registry, so every cancel would silently do nothing and every start would return 202 over a
/// pipeline the endpoints could never see again. BackendHostWiringTests pins the identity.
public sealed class PipelineSupervisor(
    IRecordStore store, IRunStore runs, PipelineRunner runner, ILogger<PipelineSupervisor> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> _live = new();

    /// Guards the check-then-add in <see cref="TryStart"/>. ConcurrentDictionary makes each operation
    /// atomic, not the PAIR of them, and two concurrent starts that both passed the check would run two
    /// pipelines over one project — which is exactly the thing the 409 exists to prevent.
    private readonly object _gate = new();

    /// OUR shutdown, not the one BackgroundService hands ExecuteAsync — because a pipeline can be started by
    /// a request BEFORE ExecuteAsync ever runs. Kestrel is already listening by then (it is registered as a
    /// hosted service ahead of this one), so a supervisor that only learned its host token inside ExecuteAsync
    /// would run those early pipelines under CancellationToken.None: unstoppable at shutdown, and killed
    /// mid-write instead of unwound.
    private readonly CancellationTokenSource _shutdown = new();

    /// False when a pipeline is already live for this project — the endpoint turns that into a 409.
    public bool TryStart(string projectId)
    {
        // RunContinuationsAsynchronously, and completed OUTSIDE the lock: without both, the pipeline's first
        // synchronous chunk would run on this thread, inside the lock.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_live.ContainsKey(projectId)) return false;
            _live[projectId] = RunAsync(projectId, release.Task);
        }
        release.SetResult();
        return true;
    }

    public bool IsRunning(string projectId) => _live.ContainsKey(projectId);

    /// The live pipeline's task, or a completed one when nothing is running. For a shutdown drain and for a
    /// test that needs to wait for the pipeline it just started.
    public Task Completion(string projectId) =>
        _live.TryGetValue(projectId, out var task) ? task : Task.CompletedTask;

    public bool CancelRun(string runId) => runner.CancelRun(runId);

    /// <param name="release">completed by TryStart once the registry entry EXISTS. Without this gate a
    /// pipeline that finished synchronously would run its `finally` — and remove itself — before TryStart
    /// ever added it, leaving a phantom entry that makes the project permanently unstartable.</param>
    private async Task RunAsync(string projectId, Task release)
    {
        await release;
        try
        {
            await runner.RunAsync(projectId, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. The runner rethrew rather than stamping, deliberately, so the stage stays
            // `running` and the next boot's resume picks it up. Nothing to record here.
            logger.LogInformation("pipeline for {ProjectId} stopped with the host", projectId);
        }
        catch (Exception e)
        {
            // Everything a STAGE can throw is already caught by the runner and stamped `failed`. Reaching here
            // means the trail itself failed — and the slot must still be released, or one bad run makes the
            // project permanently unstartable with a 409 as its only symptom.
            logger.LogError(e, "pipeline for {ProjectId} died", projectId);
        }
        finally
        {
            lock (_gate) _live.TryRemove(projectId, out _);
        }
    }

    /// Every project holding a `running` stage, re-entered. The orphaned run is stamped FIRST so the trail
    /// shows the gap: a run that simply reappeared would let a half-finished analysis read as one that ran
    /// cleanly.
    ///
    /// Two things are deliberately NOT stamped. A project this process is already running (a request that
    /// beat the boot resume to it) is skipped whole — it is not stalled, it is live. And within a project,
    /// a run the RUNNER still holds is not orphaned by definition: `PipelineRunner.IsLive` is registered
    /// before the run doc is ever written, so "persisted" implies "registered" and the check cannot see a
    /// live run as a dead one.
    public async Task ResumeAllAsync(CancellationToken ct)
    {
        foreach (var project in await store.GetProjectsAsync(ct))
        {
            if (!project.Stages.Values.Any(s => s.Status == StageStatus.Running)) continue;
            if (IsRunning(project.ProjectId)) continue;

            foreach (var run in await runs.ListAsync(project.ProjectId, null, ct))
            {
                if (run.Outcome != RunOutcome.Running || runner.IsLive(run.Id)) continue;
                run.Outcome = RunOutcome.Interrupted;
                run.EndedAt = DateTimeOffset.UtcNow.ToString("O");
                run.Error = "the process running this stage stopped";
                await runs.UpsertAsync(run, ct);
            }

            logger.LogInformation("resuming pipeline for {ProjectId}", project.ProjectId);
            TryStart(project.ProjectId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ResumeAllAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A resume that throws must not take the host down — the app still serves reads, and the
            // operator can restart a stalled project by hand. Silence is the thing to avoid (§11).
            logger.LogError(e, "the boot resume failed — stalled projects were not re-entered");
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* the host is stopping */ }

        // Cancel every live pipeline and give them a moment to unwind. The runner treats a HOST cancel as
        // "leave the stage resumable" and does not stamp it — but a regulatory child still closes its own run
        // on the way out, and that write needs the process to still be here. The host's own shutdown timeout
        // bounds this outer wait; the inner one keeps a wedged pipeline from consuming all of it.
        await _shutdown.CancelAsync();
        try { await Task.WhenAll([.. _live.Values]).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (Exception e) { logger.LogWarning(e, "a pipeline did not unwind before shutdown"); }
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        base.Dispose();
    }
}
