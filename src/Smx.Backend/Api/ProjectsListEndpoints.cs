using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// GET /projects — the estate list the frontend's landing page reads. Newest-first, each row carrying
/// the stage spine and both gate statuses: the "Needs signing" card is computed from the gates, and
/// nothing less than the record can be allowed to back it.
public static class ProjectsListEndpoints
{
    public static void MapProjectsListEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative — same reason as ProjectEndpoints: a test host that
        // never registers IRecordStore would otherwise fail to build this route, breaking routing app-wide.
        app.MapGet("/projects", async ([FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var projects = await store.GetProjectsAsync(ct: ct);
            var list = new List<object>(projects.Count);
            foreach (var p in projects)   // newest-first, straight from the store's ORDER BY
            {
                var regulatory = await store.GetGateAsync(p.ProjectId, GateTypes.Regulatory, ct);
                var vp = await store.GetGateAsync(p.ProjectId, GateTypes.Vp, ct);
                list.Add(new
                {
                    p.ProjectId, p.Client, p.Product, p.CreatedAt, p.Stages,
                    // A Dictionary, not an anonymous object: Json.Options drops null PROPERTIES
                    // (WhenWritingNull), but an absent gate must serialize as an EXPLICIT null —
                    // "no gate yet" is a value the frontend reads, not a field it has to infer —
                    // and dictionary entries are exempt from the ignore condition.
                    gates = new Dictionary<string, string?>
                    {
                        [GateTypes.Regulatory] = regulatory?.Status,
                        [GateTypes.Vp] = vp?.Status,
                    },
                });
            }
            return Results.Json(list, Json.Options);
        });

        // GET /projects/{id}/dashboard — §7's re-entry aggregation: what's blocked and ON WHOM, what's
        // ready to continue, what needs signing. A PURE projection over the ProjectDoc + the two GateDocs —
        // every fact already lives in StageState.Status/.Error and the gate records; nothing is stored.
        app.MapGet("/projects/{projectId}/dashboard",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var project = await store.GetProjectAsync(projectId, ct);
            if (project is null) return Results.NotFound();

            // Blocked-on-whom. The owner mapping is the point: the operator collects offline judgments
            // from real people (Law 6), and a dashboard that says "operator" for the physicist's number
            // sends them chasing themselves. needs-review/failed ARE the operator's ball — with the
            // stage's Error as the detail, because an error nobody surfaces is a stall nobody notices (§11).
            var blocked = new List<object>();
            foreach (var stage in Stages.All)
            {
                if (!project.Stages.TryGetValue(stage, out var state)) continue;
                var owner = state.Status switch
                {
                    "awaiting-physics" => "physics",
                    "awaiting-RE" => "R.E.",
                    "awaiting-operator" => "operator",
                    "awaiting-VP" => "VP R&D",
                    "awaiting-samples" => "client",
                    "needs-review" or "failed" => "operator",
                    // Any awaiting-* this mapping doesn't know yet still SURFACES — a park that silently
                    // drops off the blocked list is a stall nobody notices (§11). The operator is the
                    // honest fallback owner: they triage every park anyway.
                    var s when s.StartsWith("awaiting-") => "operator",
                    _ => null,
                };
                if (owner is not null)
                    blocked.Add(new { stage, on = owner, detail = state.Error });
            }

            // Ready to continue: a pending stage whose upstream neighbour is done. Stages.All IS the
            // pipeline order, and the first stage has no upstream — a fresh project's next action is intake.
            var readyToContinue = new List<string>();
            for (var i = 0; i < Stages.All.Length; i++)
            {
                if (project.Stages.GetValueOrDefault(Stages.All[i])?.Status != "pending") continue;
                if (i == 0 || project.Stages.GetValueOrDefault(Stages.All[i - 1])?.Status == "done")
                    readyToContinue.Add(Stages.All[i]);
            }

            // Needs signing: each UNAPPROVED gate (a signed gate needs nothing), with armability from the
            // REAL predicates — the same ones the signing endpoints enforce — so this card never advertises
            // a gate the POST would refuse. Blockers surface verbatim: a blocker the operator cannot
            // locate is a gate they cannot ever arm.
            var needsSigning = new List<object>();
            var regGate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            var vpGate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            if (regGate?.Status != "approved" || vpGate?.Status != "approved")
            {
                var verdicts = await store.GetVerdictsAsync(projectId, ct);
                var candidates = await store.GetCandidatesAsync(projectId, ct);
                if (regGate?.Status != "approved")
                {
                    // Mirrors GET /gate/regulatory: completeness first (no candidates ⇒ nothing screened
                    // yet), then RegulatoryGate.Armable over the live cells.
                    var complete = candidates is not null && MatrixAssembler.IsComplete(candidates, verdicts);
                    var (armed, blockers) = candidates is null
                        ? (Ok: false, Blockers: (IReadOnlyList<string>)[])
                        : RegulatoryGate.Armable(candidates, verdicts);
                    needsSigning.Add(new
                    {
                        gate = GateTypes.Regulatory,
                        armable = complete && armed,
                        blockers = complete ? blockers : blockers.Prepend("incomplete: not every candidate has a verdict yet").ToList(),
                    });
                }
                if (vpGate?.Status != "approved")
                {
                    // Mirrors GET /gate/vp, coverage re-check included: the POST additionally 422s on
                    // absent candidates and on regulatory-coverage blockers, so an armable computed from
                    // VpGate.Armable alone would advertise a gate the POST refuses.
                    var decision = await store.GetDecisionAsync(projectId, ct);
                    var (armed, blockers) = VpGate.Armable(regGate, decision);
                    IReadOnlyList<string> uncovered = candidates is null
                        ? ["no candidates on file — there is no analysis under the regulatory signature"]
                        : RegulatoryGate.Armable(candidates, verdicts).Blockers;
                    // ...and the POST's park guard (Task 15(d)), for the same reason: a Dosing revision
                    // resets Decision to `pending` with the STALE DecisionDoc still on file — this card
                    // must not invite a signature the POST refuses.
                    var notParked = VpGate.ParkBlocker(project.Stages.GetValueOrDefault(Stages.Decision)?.Status);
                    needsSigning.Add(new
                    {
                        gate = GateTypes.Vp,
                        armable = armed && uncovered.Count == 0 && notParked is null,
                        blockers = blockers.Concat(uncovered).Concat(notParked is null ? [] : new[] { notParked }).ToList(),
                    });
                }
            }

            return Results.Json(new { projectId, blocked, readyToContinue, needsSigning }, Json.Options);
        });
    }
}
