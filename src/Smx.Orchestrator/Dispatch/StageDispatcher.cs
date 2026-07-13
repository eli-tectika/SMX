using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Knowledge;

namespace Smx.Orchestrator.Dispatch;

/// Reacts to record changes. Change feed is at-least-once: every branch must be idempotent
/// (re-check store state before acting) and every write an upsert.
public sealed class StageDispatcher(
    IRecordStore store, IAgentRuns agents, ILearnedConclusionWriter conclusions, int regulatoryParallelism)
{
    public async Task OnRecordChangedAsync(object doc, CancellationToken ct)
    {
        switch (doc)
        {
            case ProjectDoc p: await OnProjectAsync(p, ct); break;
            case ConstraintsDoc c: await OnConstraintsAsync(c, ct); break;
            case CandidatesDoc cd: await OnCandidatesAsync(cd, ct); break;
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case GateDoc g: await OnGateAsync(g, ct); break;
            case RevisionDoc r: await OnRevisionAsync(r, ct); break;
            case MatrixDoc: break; // terminal
        }
    }

    private async Task OnProjectAsync(ProjectDoc p, CancellationToken ct)
    {
        if (p.Stages[Stages.Intake].Status != "pending") return;
        if (await store.GetConstraintsAsync(p.ProjectId, ct) is not null) return;
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
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnConstraintsAsync(ConstraintsDoc c, CancellationToken ct)
    {
        if (await store.GetCandidatesAsync(c.ProjectId, ct) is not null) return; // idempotency
        await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            // Known-candidate mode: bypass the Discovery agent when the operator/eval supplied candidates.
            if (c.ProvidedCandidates.Count > 0)
            {
                await store.UpsertCandidatesAsync(new CandidatesDoc
                {
                    Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
                    Substances = [.. c.ProvidedCandidates],
                }, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
                return;
            }
            var result = await agents.RunDiscoveryAsync(c, null, ct);
            if (result.Succeeded)
            {
                await store.UpsertCandidatesAsync(result.Output!, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnCandidatesAsync(CandidatesDoc cd, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(cd.ProjectId, ct);
        if (constraints is null) return;
        var existing = (await store.GetVerdictsAsync(cd.ProjectId, ct)).Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = cd.Substances.Where(s => s.Tier != "C" && !existing.Contains((s.Cas, s.ComponentId))).ToList();
        if (missing.Count == 0) { await TryAssembleAsync(cd.ProjectId, ct); return; }

        await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "running"; s.Attempts++; }, ct);
        using var gate = new SemaphoreSlim(regulatoryParallelism);
        var tasks = missing.Select(async candidate =>
        {
            await gate.WaitAsync(ct);
            try
            {
                try
                {
                    var result = await agents.RunRegulatoryAsync(constraints, candidate, null, ct);
                    var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                    {
                        Id = RecordIds.Verdict(cd.ProjectId, candidate.Cas, candidate.ComponentId),
                        ProjectId = cd.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
                        Element = candidate.Element, Form = candidate.Form,
                        Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [],
                            0, $"agent could not produce a valid cited verdict: {result.Error}")],
                    };
                    await store.UpsertVerdictAsync(verdict, ct);
                }
                catch (Exception e)
                {
                    await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        await TryAssembleAsync(cd.ProjectId, ct);
    }

    private Task OnVerdictAsync(VerdictDoc v, CancellationToken ct) => TryAssembleAsync(v.ProjectId, ct);

    // Trusts the gate record: does NOT re-check arming/completeness here. The false-pass-safety
    // invariant is that POST /regulatory/approve (armable + IsComplete) is the ONLY writer of an
    // approved regulatory GateDoc. Do not add another writer without those two checks.
    private async Task OnGateAsync(GateDoc g, CancellationToken ct)
    {
        if (g is { GateType: GateTypes.Regulatory, Status: "approved" })
            await SetStageAsync(g.ProjectId, Stages.Regulatory,
                s => { if (s.Status == "awaiting-RE") s.Status = "done"; }, ct);
    }

    /// Revise-with-reason (Law 4). Re-runs the stage's agent with the operator's directive, voids the gate
    /// their signature no longer covers, and records what was learned.
    private async Task OnRevisionAsync(RevisionDoc r, CancellationToken ct)
    {
        // At-least-once change feed: only the first delivery acts. Marking the doc `applied` at the end
        // re-enters this handler once more, which is exactly what this guard absorbs.
        if (r.Status != RevisionStatus.Pending) return;
        if (await store.GetConstraintsAsync(r.ProjectId, ct) is not { } constraints)
        {
            await FailAsync(r, "project has no constraints — there is no agent output to revise", ct);
            return;
        }

        try
        {
            var stageOutputJson = r.Stage switch
            {
                Stages.Discovery => await ReviseDiscoveryAsync(constraints, r, ct),
                Stages.Regulatory => await ReviseRegulatoryAsync(constraints, r, ct),
                _ => throw new InvalidOperationException($"stage '{r.Stage}' is not revisable"),
            };
            r.ConclusionId = await WriteConclusionAsync(r, constraints, stageOutputJson, ct);
            r.Status = RevisionStatus.Applied;
            r.AppliedAt = DateTimeOffset.UtcNow.ToString("O");
            r.Error = null;
            await store.UpsertRevisionAsync(r, ct);
        }
        catch (Exception e)
        {
            await FailAsync(r, e.Message, ct);
        }
    }

    private async Task<string> ReviseDiscoveryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var result = await agents.RunDiscoveryAsync(c, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the discovery agent could not apply the revision: {result.Error}");

        // ORDER MATTERS. Void the gate BEFORE the new output lands: the upsert below is a change-feed event
        // that re-enters TryAssembleAsync, and if that found the gate still `approved` it would mark
        // Regulatory `done` over the new, unreviewed verdicts.
        await VoidRegulatoryGateAsync(r, ct);
        await store.UpsertCandidatesAsync(result.Output!, ct);   // same id ⇒ replaces; the feed re-fans Regulatory
        return JsonSerializer.Serialize(result.Output!.Substances, Json.Options);
    }

    private async Task<string> ReviseRegulatoryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var candidates = await store.GetCandidatesAsync(r.ProjectId, ct)
            ?? throw new InvalidOperationException("no candidates — Regulatory has not run for this project");
        var candidate = candidates.Substances.FirstOrDefault(s => s.Cas == r.Cas && s.ComponentId == r.ComponentId)
            ?? throw new InvalidOperationException(
                $"the revision targets {r.Cas}|{r.ComponentId}, which is not a candidate in this project");

        var result = await agents.RunRegulatoryAsync(c, candidate, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the regulatory agent could not apply the revision: {result.Error}");

        await VoidRegulatoryGateAsync(r, ct);
        // The agent's fresh VerdictDoc carries EvidenceReviewed=false and Determination=null by default, so
        // replacing the old one CLEARS the operator's prior ruling — deliberately. That ruling was made
        // against the verdict this one replaces; RegulatoryGate.Armable will now block the gate until the
        // operator opens this item again.
        await store.UpsertVerdictAsync(result.Output!, ct);
        return JsonSerializer.Serialize(result.Output!, Json.Options);
    }

    /// A gate is an operator's signature over a SPECIFIC analysis, and the revision just replaced that
    /// analysis. Leaving the signature standing is the false pass: TryAssembleAsync will not lower a stage
    /// that already reached `done`, so an approved-and-done Regulatory stage would silently absorb verdicts
    /// the operator never reviewed. Void it and make them sign again.
    private async Task VoidRegulatoryGateAsync(RevisionDoc r, CancellationToken ct)
    {
        // BreaksRegulatoryGate is a partial function — it throws for a non-revisable stage rather than
        // returning the dangerous `false`. We only reach here for a revisable stage, but ask explicitly.
        if (!RevisionEffects.IsRevisable(r.Stage) || !RevisionEffects.BreaksRegulatoryGate(r.Stage)) return;

        if (await store.GetGateAsync(r.ProjectId, GateTypes.Regulatory, ct) is { Status: "approved" } gate)
        {
            gate.Status = "locked";
            gate.ApprovedAt = null;
            await store.UpsertGateAsync(gate, ct);
        }
        await SetStageAsync(r.ProjectId, Stages.Regulatory,
            s => { if (s.Status == "done") s.Status = "awaiting-RE"; }, ct);
    }

    private async Task<string> WriteConclusionAsync(
        RevisionDoc r, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct)
    {
        var kind = RevisionEffects.ConclusionKind(r.Stage);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var distilled = await agents.RunConclusionAsync(r, constraints, stageOutputJson, ct);

        var doc = new LearnedConclusionDoc
        {
            Id = KnowledgeIds.RevisionConclusion(kind, r.Id),
            Kind = kind,
            // If the distiller could not produce a valid conclusion we still record the operator's reason
            // VERBATIM rather than dropping it — silently discarding the "why" would break Law 4's promise
            // that every change-with-a-reason teaches the system something.
            Scope = distilled.Succeeded ? distilled.Output!.Scope : new(null, null, null, null, null, null),
            Finding = distilled.Succeeded
                ? distilled.Output!.Finding
                : $"Operator revised {r.Stage} — {r.Target}: {r.Reason}",
            Confidence = distilled.Succeeded ? distilled.Output!.Confidence : 0.5,
            // Provenance is CODE-owned, always. The operator's reason must reach the knowledge layer word
            // for word: a model permitted to paraphrase "overlaps the Ti K-beta line" into "improved
            // tiering" would erase the only part of the record that is worth keeping.
            Provenance = new([r.ProjectId], [$"revision {r.Id} — target: {r.Target} — operator reason: {r.Reason}"]),
            CreatedAt = now,
        };
        await conclusions.WriteAsync(doc, ct);
        return doc.Id;
    }

    private async Task FailAsync(RevisionDoc r, string error, CancellationToken ct)
    {
        r.Status = RevisionStatus.Failed;
        r.Error = error;
        await store.UpsertRevisionAsync(r, ct);
    }

    private async Task TryAssembleAsync(string projectId, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(candidates, verdicts)) return;

        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        var regStatus = gate?.Status == "approved" ? "done" : "awaiting-RE";
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }, ct);

        if (await store.GetMatrixAsync(projectId, ct) is null)
        {
            var componentIds = constraints.Components.Select(k => k.Id).ToList();
            await store.UpsertMatrixAsync(
                MatrixAssembler.Assemble(candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
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
