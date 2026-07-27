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

    /// Work that is deliberately NOT project-exclusive — today, a chat turn. Keyed by a throwaway id
    /// because there is nothing to look one up by: it exists so the shutdown drain and a test can wait on
    /// it, not so anything can address it.
    private readonly ConcurrentDictionary<Guid, Task> _detached = new();

    /// Guards the check-then-add in <see cref="TryRun"/>. ConcurrentDictionary makes each operation
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
    public bool TryStart(string projectId) => TryRun(projectId, (r, ct) => r.RunAsync(projectId, ct));

    /// Project-EXCLUSIVE background work that is not (only) a pipeline pass. Today that is revise-with-
    /// reason: apply the revision, then re-enter the pipeline over what it replaced.
    ///
    /// It takes the SAME registry slot a pipeline does, deliberately. A revision re-runs a stage agent and
    /// replaces the very records a live stage is writing; two writers over one stage is exactly what the
    /// 409 on `/start` exists to prevent, and the fact that one of them arrived through a different door
    /// does not make it safe. `IsRunning` therefore reports it too, so the message door's `queued` flag and
    /// the rerun endpoint's conflict check both see a revision in flight for what it is: the project is busy.
    ///
    /// False ⇒ the project is already busy; the caller decides what that means on the wire.
    public bool TryRun(string projectId, Func<PipelineRunner, CancellationToken, Task> work)
    {
        // RunContinuationsAsynchronously, and completed OUTSIDE the lock: without both, the work's first
        // synchronous chunk would run on this thread, inside the lock.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_live.ContainsKey(projectId)) return false;
            _live[projectId] = RunAsync(projectId, work, release.Task);
        }
        release.SetResult();
        return true;
    }

    /// Work that must NOT wait for the project's pipeline: a chat turn. The operator asks "why did you drop
    /// the Zr neodecanoate?" WHILE Regulatory is screening, and an answer that arrived only once the stage
    /// finished would be the wall the conversation exists not to be — under the change feed these ran
    /// concurrently, and nothing about folding the orchestrator in changed that requirement.
    ///
    /// It is not unowned, though: it runs on the supervisor's shutdown token and is tracked for the drain,
    /// so a turn in flight is unwound at stop rather than killed mid-write.
    public void RunDetached(string what, Func<PipelineRunner, CancellationToken, Task> work)
    {
        var id = Guid.NewGuid();
        // The same release gate TryRun uses, for the same reason: work that completes synchronously would
        // otherwise remove an entry that had not been added yet, and leak the entry that lands after it.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _detached[id] = DetachedAsync(id, what, work, release.Task);
        release.SetResult();
    }

    /// Take the project's slot for the sole purpose of running the post-work drain — the work itself is a
    /// no-op, because the drain lives in <see cref="RunAsync"/>'s finally and there is exactly one copy of
    /// it. This is what a chat turn calls once it has finished: its `apply_revision` may have queued a
    /// revision, and on an idle project nothing else is coming to apply it.
    ///
    /// It RETRIES, briefly, and that is not politeness — it closes the one real race. The slot-holder reads
    /// the pending list just before it releases; a revision written in the sliver between that read and the
    /// release would be missed by the holder AND refused here. A retry a moment later finds the slot free.
    /// It does not retry long enough to outwait a running pipeline, and it does not need to: a revision
    /// written while a pipeline is genuinely running is written BEFORE that pipeline's own drain reads the
    /// list, so the holder catches it.
    public async Task DrainWhenFreeAsync(string projectId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < DrainSlotAttempts; attempt++)
        {
            if (TryRun(projectId, (_, _) => Task.CompletedTask))
            {
                await Completion(projectId);
                return;
            }
            await Task.Delay(DrainSlotRetry, ct);
        }
        // The slot never came free. Not a failure: the holder's own post-work drain is the backstop, and it
        // cannot miss a revision that was already durable before it started reading.
        logger.LogInformation(
            "{ProjectId} stayed busy through the opportunistic drain; leaving it to the slot-holder", projectId);
    }

    /// Sized for the release race above, NOT for a pipeline's duration: five attempts a fifth of a second
    /// apart. The common case — an idle project — succeeds on attempt zero with no delay at all.
    private const int DrainSlotAttempts = 5;
    private static readonly TimeSpan DrainSlotRetry = TimeSpan.FromMilliseconds(200);

    public bool IsRunning(string projectId) => _live.ContainsKey(projectId);

    /// The live pipeline's task, or a completed one when nothing is running. For a shutdown drain and for a
    /// test that needs to wait for the pipeline it just started.
    public Task Completion(string projectId) =>
        _live.TryGetValue(projectId, out var task) ? task : Task.CompletedTask;

    /// Every detached turn currently in flight. For the shutdown drain, and for a test that needs to wait
    /// for the turn the endpoint it just called started — the endpoint registers the task before it returns
    /// its response, so a caller holding that response is guaranteed to see it here.
    public Task DetachedCompletion() => Task.WhenAll(_detached.Values.ToArray());

    public bool CancelRun(string runId) => runner.CancelRun(runId);

    /// <param name="release">completed by TryRun once the registry entry EXISTS. Without this gate a
    /// pipeline that finished synchronously would run its `finally` — and remove itself — before TryRun
    /// ever added it, leaving a phantom entry that makes the project permanently unstartable.</param>
    private async Task RunAsync(string projectId, Func<PipelineRunner, CancellationToken, Task> work, Task release)
    {
        await release;
        try
        {
            await work(runner, _shutdown.Token);
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
            // THE DRAIN, and its position is the whole point: inside the slot, after the work, BEFORE the
            // slot is released. A chat turn's `apply_revision` writes a durable pending RevisionDoc from
            // outside any slot; if the project was busy at the time, this is what picks it up. Running it
            // here means the busy case needs no coordination at all — whoever holds the slot cleans up on
            // the way out.
            //
            // It runs even when `work` FAILED: a revision the failed work never got to is exactly the one
            // that would otherwise sit pending forever, holding the VP gate shut. It does NOT run on
            // shutdown — a drain started as the host stops would be cancelled mid-revision, and the next
            // boot's resume is the honest place for it.
            try
            {
                if (!_shutdown.IsCancellationRequested)
                    await runner.DrainRevisionsAsync(projectId, _shutdown.Token);
            }
            catch (Exception e)
            {
                // Must not escape: the slot removal below has to happen, or one bad drain makes the project
                // permanently unstartable with a 409 as its only symptom.
                logger.LogError(e, "the revision drain for {ProjectId} failed", projectId);
            }
            lock (_gate) _live.TryRemove(projectId, out _);
        }
    }

    /// The detached twin of <see cref="RunAsync"/>. It swallows the same two failure classes for the same
    /// reason — a chat turn that throws must not take the host down — but it holds no project slot, so
    /// there is nothing here that a failure could make permanently unstartable.
    private async Task DetachedAsync(
        Guid id, string what, Func<PipelineRunner, CancellationToken, Task> work, Task release)
    {
        await release;
        try
        {
            await work(runner, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("{What} stopped with the host", what);
        }
        catch (Exception e)
        {
            // PipelineRunner.OnChatMessageAsync stamps its own message `failed`, so reaching here means
            // something outside the turn broke. Log it: silence is the one thing §11 forbids.
            logger.LogError(e, "{What} died", what);
        }
        finally
        {
            _detached.TryRemove(id, out _);
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
        // Detached turns are drained with the pipelines: a chat turn is mid-write to the same record store.
        await _shutdown.CancelAsync();
        try
        {
            await Task.WhenAll([.. _live.Values, .. _detached.Values])
                .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception e) { logger.LogWarning(e, "a pipeline did not unwind before shutdown"); }
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        base.Dispose();
    }
}
