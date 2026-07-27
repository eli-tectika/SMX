using System.Text.Json.Serialization;
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
        // IRunStore is NULLABLE here, unlike everywhere else it appears. It is only registered on a host
        // with Cosmos configured, and the live line is an enhancement over a list that must keep working:
        // the estate list is how the operator finds anything at all, so it must not 500 because run
        // telemetry happens to be unavailable. A nullable minimal-API service parameter resolves through
        // GetService rather than GetRequiredService, so this degrades to "no live line" instead of a 500.
        app.MapGet("/projects", async (
            [FromServices] IRecordStore store, [FromServices] IRunStore? runs, CancellationToken ct) =>
        {
            var projects = await store.GetProjectsAsync(ct: ct);
            var list = new List<object>(projects.Count);
            foreach (var p in projects)   // newest-first, straight from the store's ORDER BY
            {
                var regulatory = await store.GetGateAsync(p.ProjectId, GateTypes.Regulatory, ct);
                var vp = await store.GetGateAsync(p.ProjectId, GateTypes.Vp, ct);
                list.Add(new ProjectListRow
                {
                    ProjectId = p.ProjectId, Client = p.Client, Product = p.Product,
                    CreatedAt = p.CreatedAt, Stages = p.Stages,
                    ActiveRun = await ActiveRunAsync(p, runs, ct),
                    // A Dictionary, not an anonymous object: Json.Options drops null PROPERTIES
                    // (WhenWritingNull), but an absent gate must serialize as an EXPLICIT null —
                    // "no gate yet" is a value the frontend reads, not a field it has to infer —
                    // and dictionary entries are exempt from the ignore condition.
                    Gates = new Dictionary<string, string?>
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

            // Ready to continue: a pending stage whose upstream neighbour is done. Stages.SPINE, not
            // Stages.All: the spine is the operator-facing order, and the first stage has no upstream — a
            // fresh project's next action is intake. Pool is in `All` (it is chattable) but not here, because
            // it legitimately SKIPS without ever being stamped — walking it would park this list on `pool`
            // forever on every project that provided its own candidates.
            var readyToContinue = new List<string>();
            for (var i = 0; i < Stages.Spine.Length; i++)
            {
                if (project.Stages.GetValueOrDefault(Stages.Spine[i])?.Status != "pending") continue;
                if (i == 0 || project.Stages.GetValueOrDefault(Stages.Spine[i - 1])?.Status == "done")
                    readyToContinue.Add(Stages.Spine[i]);
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
                    // must not invite a signature the POST refuses. Nor while a Dosing/Decision revision
                    // is still PENDING (F1 layer 3): the decision may be about to change.
                    var notParked = VpGate.ParkBlocker(project.Stages.GetValueOrDefault(Stages.Decision)?.Status);
                    var inFlight = VpGate.PendingRevisionBlocker(await store.GetRevisionsAsync(projectId, ct));
                    needsSigning.Add(new
                    {
                        gate = GateTypes.Vp,
                        armable = armed && uncovered.Count == 0 && notParked is null && inFlight is null,
                        blockers = blockers.Concat(uncovered)
                            .Concat(notParked is null ? [] : new[] { notParked })
                            .Concat(inFlight is null ? [] : new[] { inFlight }).ToList(),
                    });
                }
            }

            return Results.Json(new { projectId, blocked, readyToContinue, needsSigning }, Json.Options);
        });
    }

    /// What this project is doing RIGHT NOW, in words — the card's live line. Null when nothing runs.
    ///
    /// Gated on the project's own stage statuses before touching IRunStore, and that is the whole
    /// design: the list page renders every project in the estate, and a runs query per row would make
    /// the landing screen pay for telemetry it usually has nothing to say about. A project that is
    /// parked, settled or not started — the common case — costs zero extra reads.
    ///
    /// The TOP-LEVEL run is preferred over a child. Regulatory fans out per substance, and a card
    /// that named one of fourteen would be picking an arbitrary one and implying it was the work;
    /// the parent is the honest answer to "what stage is busy". `agent: null` is carried, not hidden,
    /// so the card can decline to imply a model on a deterministic stage.
    private static async Task<ActiveRun?> ActiveRunAsync(ProjectDoc p, IRunStore? runs, CancellationToken ct)
    {
        if (runs is null) return null;
        var stage = p.Stages.FirstOrDefault(s => s.Value.Status == StageStatus.Running).Key;
        if (stage is null) return null;

        var forStage = await runs.ListAsync(p.ProjectId, stage, ct);
        var running = forStage.Where(r => r.Outcome == RunOutcome.Running).ToList();
        if (running.Count == 0) return null;

        // ListAsync is oldest-first, so LastOrDefault is the newest of each candidate set.
        var run = running.LastOrDefault(r => r.ParentRunId is null) ?? running[^1];
        return new ActiveRun
        {
            Stage = run.Stage,
            Agent = run.Agent,
            LastStep = run.Steps.Count == 0 ? null : run.Steps[^1].Text,
        };
    }
}

/// One row of GET /projects.
///
/// Declared rather than anonymous for the same reason <see cref="ActiveRun"/> is: `activeRun` is null
/// on every idle project, and `Json.Options` sets `DefaultIgnoreCondition = WhenWritingNull`, which
/// would drop the key entirely on exactly the rows that most need to say "nothing is happening here".
/// Absent and null are not the same claim, and the row should make the one it means.
public sealed record ProjectListRow
{
    public required string ProjectId { get; init; }
    public required string Client { get; init; }
    public required string Product { get; init; }
    public required string CreatedAt { get; init; }
    public required Dictionary<string, StageState> Stages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public ActiveRun? ActiveRun { get; init; }
    public required Dictionary<string, string?> Gates { get; init; }
}

/// The live line on a project card.
///
/// A declared type rather than an anonymous object for one reason: <see cref="Json.Options"/> sets
/// `DefaultIgnoreCondition = WhenWritingNull`, which would silently drop exactly the two fields whose
/// null the client reads. `agent === null` means "a deterministic stage, do not imply a model"; an
/// ABSENT `agent` is indistinguishable from that in JS only by accident, and a run with no steps yet
/// must say so rather than vanish. Present-and-null is the contract.
public sealed record ActiveRun
{
    public required string Stage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Agent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? LastStep { get; init; }
}
