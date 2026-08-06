using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Backend.Pipeline;

namespace Smx.Backend.Api;

public sealed record AmendmentRequest(
    string Field, string Value, string Reason, string? ComponentId = null,
    bool ConfirmSignatureVoid = false);

public static class AmendmentEndpoints
{
    /// The operator's door for changing a REQUIREMENT after intake (redesign spec §9).
    ///
    /// Distinct from `record_answer` (a pre-intake gap-fill, which refuses once constraints exist) and from
    /// `apply_revision` (which revises an AGENT'S OUTPUT on one stage). This changes a fact about the
    /// customer's product, upstream of every agent, and invalidates a SET of stages.
    ///
    /// Law 4 survives intact: the operator never hand-mutates an analytical result. They state the new
    /// requirement and WHY, the reason is recorded, and the agents re-derive everything downstream.
    public static void MapAmendmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/projects/{projectId}/amendments",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetProjectAsync(projectId, ct) is { } p
                ? Results.Json(new { projectId, amendments = p.Amendments }, Json.Options)
                : Results.NotFound());

        app.MapPost("/projects/{projectId}/amendments",
            async (string projectId, AmendmentRequest req, [FromServices] IRecordStore store,
                   [FromServices] PipelineSupervisor? supervisor, CancellationToken ct) =>
        {
            // The reason is MANDATORY and checked first. An amendment with no reason is a direct edit with
            // extra steps, and the whole mechanism exists so the record can answer "why does this project
            // screen against Japan?" a year from now.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new
                {
                    error = "an amendment needs the operator's reason. What changed on the customer's side?",
                });

            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();
            if (await store.GetConstraintsAsync(projectId, ct) is not { } constraints)
                return Results.UnprocessableEntity(new
                {
                    error = "this project has no constraints yet — intake has not run. There is nothing to " +
                            "amend; the interview is still the place to change the requirements.",
                });

            // Scope FIRST, before anything is written: an undeclared field throws here rather than silently
            // amending the record and re-running nothing (RerunScope.For).
            IReadOnlyList<string> scope;
            try { scope = RerunScope.For(req.Field); }
            catch (ArgumentOutOfRangeException ex)
            { return Results.UnprocessableEntity(new { error = ex.Message }); }

            var regGate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            var vpGate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            var atRisk = RerunScope.SignaturesAtRisk(
                scope, regGate?.Status == "approved", vpGate?.Status == "approved");

            // THE ONE PLACE AN AMENDMENT STOPS TO ASK (spec §9.4). Nothing waits in this system any more —
            // except that silently un-signing a human's approval is not something software should do
            // quietly. Unsigned projects amend and re-run with no interruption at all.
            if (atRisk.Count > 0 && !req.ConfirmSignatureVoid)
                return Results.Conflict(new
                {
                    error = "this amendment re-runs a stage whose signature is already on file. Confirm to " +
                            "proceed; the signature will be voided and must be given again.",
                    voids = atRisk,
                    rerun = scope,
                });

            var componentId = req.ComponentId ?? "";
            var from = Amendments.Previous(constraints, componentId, req.Field);

            var (patched, error) = Amendments.Apply(constraints, componentId, req.Field, req.Value);
            if (patched is null) return Results.UnprocessableEntity(new { error });

            await store.UpsertConstraintsAsync(patched, ct);

            // Void the signatures the rerun invalidates — as a PAIR, exactly as the revision path does. A
            // locked gate carrying a signer would report `{status:"locked", approvedBy:"operator"}`, which a
            // screen reading the signer whenever it is non-null renders as "signed by the operator" over a
            // gate this amendment deliberately voided.
            foreach (var gateType in atRisk)
            {
                var gate = gateType == GateTypes.Regulatory ? regGate : vpGate;
                if (gate is null) continue;
                gate.Status = "locked";
                gate.ApprovedAt = null;
                gate.ApprovedBy = null;
                await store.UpsertGateAsync(gate, ct);
            }

            // Reset exactly the stages in the blast radius. `done` IS included — that is the point of an
            // amendment — but `running` is not: a stage mid-flight will finish over the old requirement and
            // the next pass re-runs it, which is safer than yanking the record out from under a live agent.
            foreach (var stage in scope)
            {
                if (!project.Stages.TryGetValue(stage, out var state)) continue;
                if (state.Status is StageStatus.Running) continue;
                state.Status = StageStatus.Pending;
                state.Error = null;
            }

            project.Amendments.Add(new Amendment(
                DateTimeOffset.UtcNow.ToString("O"), componentId, req.Field, from, req.Value, req.Reason,
                scope, atRisk));
            await store.UpsertProjectAsync(project, ct);

            // Re-enter the pipeline. Recording the amendment and running agents stay separate acts, so this
            // endpoint never blocks in Foundry on the caller's thread.
            supervisor?.TryStart(projectId);

            return Results.Accepted($"/projects/{projectId}/amendments", new
            {
                projectId, field = req.Field, from, to = req.Value,
                rerun = scope, voided = atRisk,
            });
        });
    }
}
