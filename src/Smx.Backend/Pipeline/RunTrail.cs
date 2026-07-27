using Microsoft.Extensions.Logging;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// Appends to the run doc, persists it, and publishes the frame. Best-effort by contract (spec D9):
/// a telemetry write must never be the thing that fails a regulatory screen, so every persist is
/// swallowed. The consequence — a run with a hole in it — is acceptable because the STAGE's own status
/// and error remain authoritative. The trail explains; it does not adjudicate.
///
/// SINGLE WRITER, by assumption and not by enforcement: `_opened` and the doc's step list are both
/// mutated without a lock. One RunTrail therefore belongs to exactly one sequential body — the
/// regulatory fan-out gives every parallel branch its OWN RunDoc and its own trail, and sharing one
/// across Task.WhenAll branches would race both.
public sealed class RunTrail(RunDoc run, IRunStore store, ThreadEventHub hub, ILogger? logger = null) : IRunTrail
{
    public RunDoc Run => run;

    public async Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default)
    {
        await OpenAsync(ct); // lazy — see OpenAsync
        // The clock lives HERE, not in the record (RevisionDoc.CreatedAt rule): the domain has none.
        var step = run.Append(kind, text, DateTimeOffset.UtcNow.ToString("O"), detail);
        hub.Publish(run.ProjectId, run.Stage,
            new ThreadFrame("step", $"{run.Id}.s{step.Seq}", new { runId = run.Id, step }));
        await PersistAsync(ct);
    }

    public async Task CompleteAsync(string outcome, string? error, CancellationToken ct)
    {
        // A run can complete having written no step at all — a stage body that throws on entry (a null
        // store, an unreadable record) reaches ExecuteAsync's catch before its first StepAsync. Opening
        // here means the live subscriber sees the group appear and then fail, instead of a stray `run`
        // frame for a group it never saw. The alternative — skipping the completion for an unopened
        // trail — would make an instantly-failing stage invisible, which is precisely the failure an
        // operator most needs to see. Idempotent, so this is free on the normal path.
        await OpenAsync(ct);
        run.Outcome = outcome;
        run.Error = error;
        run.EndedAt = DateTimeOffset.UtcNow.ToString("O");
        hub.Publish(run.ProjectId, run.Stage, new ThreadFrame("run", $"{run.Id}.r",
            new { runId = run.Id, endedAt = run.EndedAt, outcome, error }));
        await PersistAsync(ct);
    }

    private bool _opened;

    /// Published before the first step so a subscriber sees the group appear immediately.
    ///
    /// LAZY, and idempotent. A stage body decides whether it has work only after reading the record,
    /// and a run doc opened for a stage that then skipped would put an empty group in the operator's
    /// timeline for every already-completed stage on every resume. So the first StepAsync opens it,
    /// and a body that returns without writing a step has opened nothing.
    public async Task OpenAsync(CancellationToken ct)
    {
        if (_opened) return;
        _opened = true;
        hub.Publish(run.ProjectId, run.Stage,
            new ThreadFrame("entry", run.Id, new { seq = 0, at = run.StartedAt, kind = "run", run }));
        await PersistAsync(ct);
    }

    /// True once anything has been written — ExecuteAsync uses it to tell "this stage ran" from
    /// "this stage skipped" without the body having to say so twice.
    public bool Opened => _opened;

    private async Task PersistAsync(CancellationToken ct)
    {
        try
        {
            await store.UpsertAsync(run, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning(e, "run trail write failed for {RunId} — the run continues", run.Id);
        }
    }
}
