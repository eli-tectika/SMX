using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);
    Task<AgentRunResult<VerdictDoc>> RunScreeningAsync(ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct);
}

/// Reacts to record changes. Change feed is at-least-once: every branch here must be
/// idempotent (re-checking store state before acting) and every write an upsert.
public sealed class StageDispatcher(IRecordStore store, IAgentRuns agents, int screeningParallelism)
{
    public async Task OnRecordChangedAsync(object doc, CancellationToken ct)
    {
        switch (doc)
        {
            case ProjectDoc p: await OnProjectAsync(p, ct); break;
            case ConstraintsDoc c: await OnConstraintsAsync(c, ct); break;
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case MatrixDoc: break; // terminal
        }
    }

    private async Task OnProjectAsync(ProjectDoc p, CancellationToken ct)
    {
        if (p.Stages[Stages.Intake].Status != "pending") return;                 // idempotency gate
        if (await store.GetConstraintsAsync(p.ProjectId, ct) is not null) return; // belt-and-braces
        await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunIntakeAsync(p, ct);
            if (result.Succeeded)
            {
                await store.UpsertConstraintsAsync(result.Output!, ct);
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
            {
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
            }
        }
        catch (Exception e)
        {
            await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnConstraintsAsync(ConstraintsDoc c, CancellationToken ct)
    {
        var existing = (await store.GetVerdictsAsync(c.ProjectId, ct)).Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = MatrixAssembler.Cells(c).Where(cell => !existing.Contains(cell)).ToList();
        if (missing.Count == 0) { await TryAssembleAsync(c.ProjectId, ct); return; }

        await SetStageAsync(c.ProjectId, Stages.Screening, s => { s.Status = "running"; s.Attempts++; }, ct);
        using var gate = new SemaphoreSlim(screeningParallelism);
        var tasks = missing.Select(async cell =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var substance = c.Substances.Single(s => s.Cas == cell.Cas);
                try
                {
                    var result = await agents.RunScreeningAsync(c, substance, cell.ComponentId, ct);
                    // needs_review is a first-class outcome: write a placeholder verdict so the
                    // matrix can complete and the operator sees exactly which cells need eyes.
                    var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                    {
                        Id = RecordIds.Verdict(c.ProjectId, cell.Cas, cell.ComponentId),
                        ProjectId = c.ProjectId, Cas = cell.Cas, ComponentId = cell.ComponentId,
                        Element = substance.Element, Form = substance.Form,
                        Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [],
                            0, $"agent could not produce a valid cited verdict: {result.Error}")],
                    };
                    await store.UpsertVerdictAsync(verdict, ct);
                }
                catch (Exception e)
                {
                    await SetStageAsync(c.ProjectId, Stages.Screening, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        await TryAssembleAsync(c.ProjectId, ct);
    }

    private Task OnVerdictAsync(VerdictDoc v, CancellationToken ct) => TryAssembleAsync(v.ProjectId, ct);

    private async Task TryAssembleAsync(string projectId, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        if (constraints is null) return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(constraints, verdicts)) return;

        var anyReview = verdicts.Any(v => v.Overall == VerdictStatus.NeedsReview);
        await SetStageAsync(projectId, Stages.Screening,
            s => { if (s.Status != "failed") s.Status = anyReview ? "needs-review" : "done"; }, ct);

        if (await store.GetMatrixAsync(projectId, ct) is null)
        {
            await store.UpsertMatrixAsync(
                MatrixAssembler.Assemble(constraints, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
        }
        await SetStageAsync(projectId, Stages.Matrix, s => s.Status = "done", ct);
    }

    private async Task SetStageAsync(string projectId, string stage, Action<StageState> mutate, CancellationToken ct)
    {
        if (await store.GetProjectAsync(projectId, ct) is not { } p) return;
        mutate(p.Stages[stage]);
        await store.UpsertProjectAsync(p, ct);
    }
}
