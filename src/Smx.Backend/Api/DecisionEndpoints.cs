using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The VP hard gate (spec §4): the VP's determination is an OPERATOR-SIGNED RECORD, and this endpoint is
/// the ONLY writer of an approved VP GateDoc — the dispatcher's close handler trusts that, the same
/// contract as the regulatory gate (see the note on StageDispatcher.OnGateAsync). Mirrors
/// POST /regulatory/approve's discipline: arm on the LIVE records, 422 with named blockers, idempotent
/// approved-timestamp.
public static class DecisionEndpoints
{
    public static void MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the note in
        // ProjectEndpoints: minimal APIs infer service-vs-body params app-wide at endpoint-build time, so a
        // missing attribute breaks routing for the WHOLE app.
        app.MapPost("/projects/{projectId}/decision/determination", async (string projectId,
            VpDeterminationRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (req.Determination is not ("approved" or "rejected"))
                return Results.UnprocessableEntity(new { error = "determination must be 'approved' or 'rejected'" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "every determination requires a reason" });

            var regGate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            var decision = await store.GetDecisionAsync(projectId, ct);
            if (VpGate.Armable(regGate, decision) is { Ok: false } blocked)
                return Results.UnprocessableEntity(new { error = "VP gate not armable", blockers = blocked.Blockers });

            // The regulatory signature is not self-proving: the gate record carries no binding to the
            // verdicts it was signed over (the TryDoseAsync rationale, StageDispatcher ~:207-228). A live
            // unreviewed non-pass verdict that appeared after the approval — a revise's leftovers, a race —
            // means the signature no longer covers the analysis this determination would sign over.
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            if (candidates is null)
                return Results.UnprocessableEntity(new
                {
                    error = "VP gate not armable",
                    blockers = (IReadOnlyList<string>)["no candidates on file — there is no analysis under the regulatory signature"],
                });
            if (RegulatoryGate.Armable(candidates, verdicts) is { Ok: false } uncovered)
                return Results.UnprocessableEntity(new
                {
                    error = "the regulatory gate is signed but no longer covers the current analysis",
                    blockers = uncovered.Blockers,
                });

            if (req.Determination is "rejected")
            {
                // The audit trail must show the VP looked and said no: a locked gate WITH the reason.
                await store.UpsertGateAsync(new GateDoc
                {
                    Id = RecordIds.Gate(projectId, GateTypes.Vp), ProjectId = projectId,
                    GateType = GateTypes.Vp, Status = "locked", Reason = req.Reason,
                }, ct);
                return Results.Ok(new { status = "rejected" });
            }

            // approve: every component must be confirmed against a REAL dosing code — a signature over a
            // nonexistent code is the false pass. Validate ALL components before stamping ANY: a 422 must
            // mean nothing happened.
            var dosing = await store.GetDosingAsync(projectId, ct);
            if (dosing is null)
                return Results.UnprocessableEntity(new { error = "dosing has not run — there are no finalized codes to confirm" });
            var byComponent = (req.Confirmations ?? []).ToDictionary(c => c.ComponentId, c => c.Code);
            foreach (var comp in decision!.Components)
            {
                if (!byComponent.TryGetValue(comp.ComponentId, out var code))
                    return Results.UnprocessableEntity(new { error = $"component '{comp.ComponentId}' has no confirmed code" });
                if (dosing.Codes.Where(c => c.ComponentId == comp.ComponentId).All(c => c.RatioSignature != code))
                    return Results.UnprocessableEntity(new { error = $"'{code}' is not a finalized code for '{comp.ComponentId}'" });
            }

            // `with` sets ONLY the Confirmed* fields: the proposal is history and is never overwritten —
            // the audit trail keeps what the agent said beside what the VP signed (Law 9).
            decision.Components = [.. decision.Components.Select(c => c with
            {
                ConfirmedCode = byComponent[c.ComponentId], ConfirmedBy = "VP R&D", ConfirmedReason = req.Reason,
            })];
            await store.UpsertDecisionAsync(decision, ct);

            var existing = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            await store.UpsertGateAsync(new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Vp), ProjectId = projectId,
                GateType = GateTypes.Vp, Status = "approved",
                ApprovedAt = existing?.Status == "approved" ? existing.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Ok(new { status = "approved" });
        });

        app.MapGet("/projects/{projectId}/gate/vp",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var regGate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            var decision = await store.GetDecisionAsync(projectId, ct);
            var (armed, blockers) = VpGate.Armable(regGate, decision);

            // The same coverage re-check the POST enforces, so this read never reports `armable` for a
            // gate the POST would refuse — a lying affordance is how a gate gets rubber-stamped. No
            // candidates ⇒ nothing to check coverage against; VpGate's own blockers carry the story.
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            IReadOnlyList<string> uncovered = candidates is null
                ? []
                : RegulatoryGate.Armable(candidates, verdicts).Blockers;

            var gate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            return Results.Json(new
            {
                status = gate?.Status ?? "locked",
                armable = armed && uncovered.Count == 0,
                blockers = blockers.Concat(uncovered).ToList(),
                approvedAt = gate?.ApprovedAt,
            }, Json.Options);
        });
    }
}

/// One component's confirmed code: `Code` is the ratio signature of the chosen MarkerCode (usually the
/// proposal, but the VP may pick any code that exists in the DosingDoc for that component — an override is
/// a valid signature).
public sealed record VpConfirmation(string ComponentId, string Code);

public sealed record VpDeterminationRequest(string Determination, string Reason, List<VpConfirmation>? Confirmations);
