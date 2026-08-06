using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// THE ONLY SIGNATURE LEFT IN THE SYSTEM (spec §4; §16.4 dropped the regulatory gate entirely). The VP's
/// determination is an OPERATOR-SIGNED RECORD, and this endpoint is the ONLY writer of an approved VP
/// GateDoc — PipelineRunner.CloseProjectAsync trusts that and re-checks nothing.
///
/// Being the last checkpoint before procurement raises what it owes the signer rather than lowering it: it
/// arms on the LIVE records, 422s with named blockers, keeps an idempotent approved-timestamp, and refuses
/// while any live flagged verdict is still unopened (EvidenceReview.Outstanding). Removing one gate and
/// leaving the other blind would be worse than either choice alone.
public static class DecisionEndpoints
{
    public static void MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the note in
        // ProjectEndpoints: minimal APIs infer service-vs-body params app-wide at endpoint-build time, so a
        // missing attribute breaks routing for the WHOLE app.
        app.MapPost("/projects/{projectId}/decision/determination", async (string projectId,
            VpDeterminationRequest req, [FromServices] IRecordStore store,
            [FromServices] PipelineRunner? runner, CancellationToken ct) =>
        {
            if (req.Determination is not ("approved" or "rejected"))
                return Results.UnprocessableEntity(new { error = "determination must be 'approved' or 'rejected'" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "every determination requires a reason" });

            // A signature answers a park (Task 15(d)): first of the record-state checks, ahead of the
            // armability refinements and every write, and covering APPROVE and REJECT alike. It closes two
            // false passes the armability checks cannot see: `pending` mid-re-pick (a Dosing revision reset
            // the stage while the STALE DecisionDoc is still on file — VpGate.Armable would happily arm
            // over it, and the in-flight re-pick would then overwrite the stamped doc under an approved
            // gate) and `done` post-close (a rejection would flip the gate locked while Procurement stays
            // Released — a revocation that revokes nothing).
            var project = await store.GetProjectAsync(projectId, ct);
            var decisionForSignability = await store.GetDecisionAsync(projectId, ct);
            if (VpGate.NotSignableBlocker(
                    project?.Stages.GetValueOrDefault(Stages.Decision)?.Status,
                    decisionForSignability?.Procurement.Status) is { } notSignable)
                return Results.UnprocessableEntity(new
                {
                    error = "VP gate not armable",
                    blockers = (IReadOnlyList<string>)[notSignable],
                });

            // ...and the window the park guard cannot see (Task 15 review F1, layer 3): the revise run is
            // minutes wide and the stage advertises `awaiting-VP` throughout. The RevisionDoc is durable
            // from POST /revise's 202 until applied/failed, so a pending Dosing/Decision revision blocks
            // the pen for the whole window — approve and reject alike, the decision may be about to change.
            if (VpGate.PendingRevisionBlocker(await store.GetRevisionsAsync(projectId, ct)) is { } inFlight)
                return Results.UnprocessableEntity(new
                {
                    error = "VP gate not armable",
                    blockers = (IReadOnlyList<string>)[inFlight],
                });

            var decision = await store.GetDecisionAsync(projectId, ct);
            if (VpGate.Armable(decision) is { Ok: false } blocked)
                return Results.UnprocessableEntity(new { error = "VP gate not armable", blockers = blocked.Blockers });

            // THE ANTI-RUBBER-STAMPING CHECK, which outlived the gate it was written for. Nobody signs a
            // regulatory analysis any more, but a live non-Pass verdict nobody has OPENED is still exactly
            // what must not travel silently into a signature that releases procurement. §16.4 is explicit
            // that this gate must show the evidence judgement needs rather than a summary — refusing while
            // flagged items are unopened is the enforceable half of that.
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            if (candidates is null)
                return Results.UnprocessableEntity(new
                {
                    error = "VP gate not armable",
                    blockers = (IReadOnlyList<string>)["no candidates on file — there is no analysis to sign over"],
                });
            if (EvidenceReview.Outstanding(candidates, verdicts) is { Count: > 0 } unopened)
                return Results.UnprocessableEntity(new
                {
                    error = "flagged regulatory findings on this analysis have not been opened",
                    blockers = unopened,
                });

            if (req.Determination is "rejected")
            {
                // The audit trail must show the VP looked and said no: a locked gate WITH the reason.
                await store.UpsertGateAsync(new GateDoc
                {
                    Id = RecordIds.Gate(projectId, GateTypes.Vp), ProjectId = projectId,
                    GateType = GateTypes.Vp, Status = "locked", Reason = req.Reason,
                    // Deliberately no ApprovedAt/ApprovedBy: the invariant is that a signer is
                    // non-null iff a timestamp is, and a refusal is not a signature.
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
            // ApprovedAt and ApprovedBy move together: updating them under different policies lets the
            // pair describe an event that never happened — a human signature stamped with a machine's
            // timestamp, or the reverse. This is the ONLY hard gate now — it releases procurement and
            // writes the Marker Library — so it must be able to say exactly who signed.
            var reaffirming = existing is { Status: "approved", ApprovedBy: GateSigners.Operator };
            var gate = new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Vp), ProjectId = projectId,
                GateType = GateTypes.Vp, Status = "approved",
                ApprovedAt = reaffirming ? existing!.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
                // The operator recording the VP's offline determination. There is no agent tool for
                // this endpoint and there never will be.
                ApprovedBy = GateSigners.Operator,
            };
            await store.UpsertGateAsync(gate, ct);

            // AND NOW THE PROJECT ACTUALLY CLOSES. Two acts, deliberately separate — record the signature,
            // then make it mean something. The second act is NOT a pipeline pass: Decision is the last stage
            // and the journey ends at this signature. OnGateAsync
            // routes an approved VP gate to CloseProjectAsync, which releases procurement, writes the Marker
            // Library entry and files the close conclusion. Until this line the signature wrote a gate
            // document and nothing else: procurement stayed `pending`, the library never learned the code,
            // and Decision sat at `awaiting-VP` under a signature that already existed, waiting for a change
            // feed that no longer runs.
            //
            // INLINE, on the caller's thread, and that is a considered choice rather than the easy one:
            //   * No agent runs. The close is store writes plus one embed-and-push for the conclusion —
            //     seconds, not the minutes in Foundry that the runner's agent-bearing paths cost.
            //   * Nothing else would ever retry it. The supervisor's boot resume re-enters projects holding
            //     a `running` stage; a project parked at `awaiting-VP` is not one, so a close dispatched to
            //     the background and lost would be lost permanently, with a signed gate as its only trace.
            //   * The response must not say "approved" over an unreleased procurement. The operator's very
            //     next click is POST /orders/{cas}, which 422s until Procurement.Status is Released.
            // Re-entrant either way: CloseProjectAsync latches on the awaiting-VP → done transition and
            // every write inside it is keyed deterministically, so a double-POST — or a re-POST after a
            // failure — converges instead of double-writing.
            if (runner is not null) await runner.OnGateAsync(gate, ct);
            return Results.Ok(new { status = "approved" });
        });

        // MSDS-before-order (spec §4): procurement is a state flag, and this precondition gates each
        // INDIVIDUAL order. The 422 chain runs release → signed-code membership → MSDS, so the error the
        // operator sees is always the FIRST rule their order breaks, and a 4xx always means no order
        // record exists.
        //
        // The gate survives the 2026-07-29 redesign; only its PREDICATE changed (D9). It used to ask
        // whether the operator had signed a review. It now asks the SDS corpus whether a validated,
        // indexed sheet exists for the CAS — which is what the signature was ever standing in for, and
        // which no longer depends on a human remembering to tick a box on a screen. Everything the
        // signature protected is still protected: procurement cannot run blind.
        app.MapPost("/projects/{projectId}/orders/{cas}", async (string projectId, string cas,
            [FromServices] IRecordStore store, [FromServices] ISdsCorpusReader corpus, CancellationToken ct) =>
        {
            var decision = await store.GetDecisionAsync(projectId, ct);
            if (decision is null || decision.Procurement.Status != ProcurementStatus.Released)
                return Results.UnprocessableEntity(new { error = "procurement is not released — only the VP gate's signature releases it" });

            // THE FLAGGED-FINDINGS CHECK, re-run at the irreversible act rather than trusted from earlier.
            // A VP GateDoc carries no binding to the verdicts it was signed over, so `approved` alone is not
            // proof the CURRENT analysis has been looked at: a fresh unreviewed non-pass verdict can land
            // under an existing signature (a revise's leftovers, a race with a late Regulatory child).
            //
            // It used to live in RunDosingAsync, where it stopped the PIPELINE. The pipeline no longer stops
            // for anything (execution-core §8), so it moved to the irreversible act — which is where it
            // always belonged. Ordering a chemical is the thing that must not happen while a flagged
            // regulatory finding on this very analysis is still unopened.
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            // Null AND empty, both. An empty CandidatesDoc is not "nothing failed" — it is every verdict
            // orphaned, i.e. no analysis at all, which is what the compliance-package export already refuses
            // for the same reason. Read as a clean bill of health it would be the most permissive state in
            // the system: EvidenceReview over zero candidates has nothing to object to.
            if (candidates is null || candidates.Substances.Count == 0)
                return Results.UnprocessableEntity(new
                {
                    error = "no candidates on file — there is no analysis behind this order",
                });
            if (EvidenceReview.Outstanding(candidates, verdicts) is { Count: > 0 } unopened)
                return Results.UnprocessableEntity(new
                {
                    error = "flagged regulatory findings on this analysis have not been opened",
                    blockers = unopened,
                });

            var dosing = await store.GetDosingAsync(projectId, ct);

            // A PROVISIONAL dosing rests on a MISSING INPUT — an estimated detection floor with no
            // physicist measurement, a substance dropped for an unknown metal loading, a run that could
            // dose nothing. The VP's signature does not retroactively re-run Dosing, so a project can
            // legitimately reach `Released` with ppms sitting above a floor nobody measured, and ordering
            // against those would buy a marker that may be undetectable in the field.
            //
            // It NO LONGER covers "a substance is here on the agent's proposal alone" (§16.4): with no
            // regulatory gate there is no writer of operator determinations, so that reason would be on
            // every dosing forever and this refusal would never lift. A refusal that cannot be cleared is
            // not a safety property, it is an outage.
            if (dosing is { Provisional: true })
                return Results.UnprocessableEntity(new
                {
                    error = "this dosing is provisional and cannot be ordered against — rerun Dosing once " +
                            "the missing measurements and inputs are on file",
                    blockers = dosing.ProvisionalReasons,
                });

            // You cannot order what the VP did not sign: the orderable set is exactly the markers of the
            // CONFIRMED codes (never the proposals — Law 9 reaches procurement too).
            var signed = decision.Components
                .Where(c => c.ConfirmedCode is not null)
                .SelectMany(c => (dosing?.Codes ?? [])
                    .Where(k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode))
                .SelectMany(k => k.Markers).Select(m => m.Cas).ToHashSet();
            if (!signed.Contains(cas))
                return Results.UnprocessableEntity(new { error = $"'{cas}' is not a marker in any VP-confirmed code — you cannot order what the VP did not sign" });

            // THE OPERATOR'S VETO, re-read at the act. Dosing composed its codes from CompliantSet, so a
            // vetoed substance could not enter one — but a veto recorded AFTER Dosing ran does not re-run
            // it, and the confirmed code goes on naming the CAS. Nothing else here would catch that: the
            // verdict is `Pass`, so it is not a flagged finding, and recording a determination sets
            // EvidenceReviewed, so it is not an unopened one either.
            //
            // ANY component's rejection refuses the whole order, not just that component's. Procurement is
            // per-CAS (Procurement.OrderedCas), so there is no such thing as buying a drum for one
            // component and not another — and CompliantSet's rule is that an operator veto always wins.
            var vetoed = verdicts
                .Where(v => v.Cas == cas && v.Determination == Determinations.Rejected)
                .Select(v => v.ComponentId).ToList();
            if (vetoed.Count > 0)
                return Results.UnprocessableEntity(new
                {
                    error = $"the operator rejected '{cas}' for {string.Join(", ", vetoed)} — a veto is not " +
                            "overruled by a confirmed code that predates it. Rerun Dosing and re-sign.",
                });

            // GetLatestForCasAsync reads the CURRENT sheets only — indexed and not superseded — which is
            // exactly "validated". An un-indexed blob in Bronze is not a sheet anyone can read, and a
            // superseded one is not the sheet the drum is shipping under.
            var sheet = await corpus.GetLatestForCasAsync(cas, ct);
            if (sheet is null)
                return Results.UnprocessableEntity(new
                {
                    // Actionable for the first time: until this design, "no MSDS" was a wall the operator
                    // could only get past by hand-rolling an HTTP POST with a base64 PDF. Fetching a sheet
                    // is now a button, so the error is allowed to name it.
                    error = $"MSDS-before-order: no safety sheet on file for '{cas}' — fetch one via " +
                            $"POST /msds/{cas}/fetch (or upload one) before ordering",
                });

            if (!decision.Procurement.OrderedCas.Contains(cas))
            {
                decision.Procurement.OrderedCas.Add(cas);
                await store.UpsertDecisionAsync(decision, ct);
            }
            return Results.Accepted($"/projects/{projectId}/decision", new { ordered = cas });
        });

        // The decision read (§7): the doc verbatim or a 404 — and the 202 Location on the determination
        // endpoint now points at a real route. ConfirmedCode serializes as an EXPLICIT null while
        // unconfirmed (see the attribute on ComponentDecision): Law 9 legible to the UI.
        app.MapGet("/projects/{projectId}/decision",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetDecisionAsync(projectId, ct) is { } decision
                ? Results.Json(decision, Json.Options)
                : Results.NotFound());

        app.MapGet("/projects/{projectId}/gate/vp",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var decision = await store.GetDecisionAsync(projectId, ct);
            var (armed, blockers) = VpGate.Armable(decision);

            // The same flagged-findings check the POST enforces, so this read never reports `armable` for a
            // gate the POST would refuse — a lying affordance is how a gate gets rubber-stamped. Absent
            // candidates are the POST's blocker verbatim: there is no analysis to sign over.
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            IReadOnlyList<string> uncovered = candidates is null
                ? ["no candidates on file — there is no analysis to sign over"]
                : EvidenceReview.Outstanding(candidates, verdicts);

            // ...and the POST's park guard, mirrored for the same reason (Task 15(d)): a stage mid-re-pick
            // or post-close reads not-armable HERE, with the blocker the POST would answer with. The
            // pending-revision guard (F1 layer 3) mirrors identically — the read must not advertise a pen
            // the POST refuses while a revision is in flight.
            var project = await store.GetProjectAsync(projectId, ct);
            var notParked = VpGate.NotSignableBlocker(
                project?.Stages.GetValueOrDefault(Stages.Decision)?.Status, decision?.Procurement.Status);
            var inFlight = VpGate.PendingRevisionBlocker(await store.GetRevisionsAsync(projectId, ct));

            var gate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            return Results.Json(new VpGateResponse(
                gate?.Status ?? "locked",
                armed && uncovered.Count == 0 && notParked is null && inFlight is null,
                blockers.Concat(uncovered)
                    .Concat(notParked is null ? [] : new[] { notParked })
                    .Concat(inFlight is null ? [] : new[] { inFlight }).ToList(),
                gate?.ApprovedAt,
                gate?.ApprovedBy), Json.Options);
        });
    }
}

/// The VP gate as the client reads it.
///
/// A named record rather than an anonymous object for one reason: `Json.Options` sets
/// `DefaultIgnoreCondition = WhenWritingNull`, which would DROP `approvedAt`/`approvedBy` from the
/// wire whenever they are null — and null is the meaningful case. A client cannot tell "the record
/// does not say who signed" from "an older API that never sent this field" if the key simply
/// vanishes. (RegulatoryGateResponse carried the same shape for the same reason, and went with the gate.)
internal sealed record VpGateResponse(
    string Status,
    bool Armable,
    IReadOnlyList<string> Blockers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ApprovedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ApprovedBy);

/// One component's confirmed code: `Code` is the ratio signature of the chosen MarkerCode (usually the
/// proposal, but the VP may pick any code that exists in the DosingDoc for that component — an override is
/// a valid signature).
public sealed record VpConfirmation(string ComponentId, string Code);

public sealed record VpDeterminationRequest(string Determination, string Reason, List<VpConfirmation>? Confirmations);
