using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// `cas` + `componentId` name the verdict a REGULATORY revision re-runs. Ignored for `discovery`, which
/// re-runs the whole component set.
public sealed record ReviseRequest(string Target, string Reason, string? Cas = null, string? ComponentId = null);

/// The front door of revise-with-reason (design §4, Law 4): the operator never hand-edits an analytical
/// result — they tell the agent WHY, the agent re-runs, and the reason becomes a Learned Conclusion.
public static class RevisionEndpoints
{
    public static void MapRevisionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every IRecordStore param below is required, not decorative — see the long
        // comment at the top of ProjectEndpoints. Without it, minimal APIs infer service-vs-body via
        // IServiceProviderIsService at endpoint-build time, across the WHOLE app's composite data source;
        // a test host that registers only IKnowledgeStore would mis-infer these as body params and break
        // routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/projects/{projectId}/stages/{stage}/revise",
            async (string projectId, string stage, ReviseRequest req, [FromServices] IRecordStore store,
                   [FromServices] PipelineSupervisor? supervisor, CancellationToken ct) =>
        {
            // Cheap checks first, and the reason check strictly before any store lookup: a revision with no
            // reason is always a 422, never a 404 — the operator must never learn "you forgot the reason"
            // only after the project id happened to be right.
            if (!RevisionEffects.IsRevisable(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"stage '{stage}' cannot be revised — only discovery, regulatory, dosing and decision produce a revisable agent output",
                });
            if (string.IsNullOrWhiteSpace(req.Target))
                return Results.UnprocessableEntity(new { error = "target is required — name what should change" });
            // Law 4. A revision without a reason is a silent edit: it mutates an analytical result and
            // teaches the system nothing, because the reason IS the seed of the Learned Conclusion.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "every revision requires a reason" });

            if (await store.GetProjectAsync(projectId, ct) is null) return Results.NotFound();

            if (stage == Stages.Discovery && await store.GetCandidatesAsync(projectId, ct) is null)
                return Results.UnprocessableEntity(new { error = "discovery has not produced candidates yet — nothing to revise" });

            if (stage == Stages.Regulatory)
            {
                // A verdict is per substance × component. Naming it is the operator's job — the dispatcher
                // must never have to guess which cell they meant.
                if (string.IsNullOrWhiteSpace(req.Cas) || string.IsNullOrWhiteSpace(req.ComponentId))
                    return Results.UnprocessableEntity(new
                    {
                        error = "a regulatory revision must name the cas and componentId of the verdict to re-run",
                    });
                if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is null)
                    return Results.UnprocessableEntity(new
                    {
                        error = $"no verdict for {req.Cas}|{req.ComponentId} in this project",
                    });
            }

            // Checked BEFORE anything is written, so a 4xx still means nothing happened. A revision re-runs
            // the stage's agent and REPLACES the record a live stage may be writing at this moment; the
            // rerun endpoint refuses for the same reason, and arriving through a different door does not
            // make two writers over one stage safe.
            //
            // The error string is written to be READ BY AN OPERATOR, not parsed: this 409 is new (the door
            // used to answer 202 unconditionally), so it is the sentence that lands on screen the first time
            // anyone hits it, and it has to say what to do next rather than only what went wrong.
            if (supervisor?.IsRunning(projectId) == true)
                return Results.Conflict(new
                {
                    error = "a run is in progress for this project — a revision replaces the very records a " +
                            "live stage is writing, so it cannot be applied now. Try again when the run " +
                            "finishes, or cancel it.",
                });

            var revisionId = RecordIds.Revision(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            var doc = new RevisionDoc
            {
                Id = revisionId,
                ProjectId = projectId,
                Stage = stage,
                Target = req.Target,
                Reason = req.Reason,
                Cas = stage == Stages.Regulatory ? req.Cas : null,
                ComponentId = stage == Stages.Regulatory ? req.ComponentId : null,
                Status = RevisionStatus.Pending,
                // ALWAYS "O". The audit trail is ordered by a LEXICOGRAPHIC sort on this field (a
                // server-side Cosmos ORDER BY on a string), which is only chronological while every
                // writer uses the same fixed-width format — see RevisionDoc.CreatedAt. This endpoint is
                // the only writer; keep it honest.
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            await store.UpsertRevisionAsync(doc, ct);

            // AND NOW SOMETHING ACTUALLY APPLIES IT. Writing the doc used to BE the dispatch — the change
            // feed picked it up, re-ran the stage, voided the gate the revision invalidates and wrote the
            // Learned Conclusion. Nothing watches the record any more, so between the feed's removal and
            // this line every "change this, and here is why" was filed and forgotten: Law 4's whole
            // mechanism, recorded and inert.
            //
            // BEHIND THE SUPERVISOR, not inline, and unlike the VP close this one is not a close call:
            // OnRevisionAsync re-runs a stage AGENT and then the conclusion distiller — two model calls, an
            // embed and a search push. That is minutes in Foundry, which no POST may hold a thread for; the
            // 202 below is the honest answer and the revision's own `pending`/`applied`/`failed` status is
            // where the operator reads the outcome.
            //
            // Then the pipeline, in the SAME slot: the revision resets what it invalidated (Matrix, and for
            // a Dosing revision, Cost and Decision) to `pending`, which under the change feed was the
            // re-trigger for those stages. Without the re-entry the project would sit holding a stale-but-
            // reset Cost and Decision that nothing re-computes.
            if (supervisor is not null && !supervisor.TryRun(projectId, async (runner, token) =>
                {
                    await runner.OnRevisionAsync(doc, token);
                    await runner.RunAsync(projectId, token);
                }))
            {
                // The narrow race the check above cannot close: a pipeline started between it and here.
                // The doc is already durable, and a `pending` revision nothing will ever apply is both a
                // lie to the operator and a permanent blocker on the VP gate (VpGate.PendingRevisionBlocker
                // holds the pen while one is in flight). Fail it honestly instead.
                doc.Status = RevisionStatus.Failed;
                doc.Error = "a pipeline started while this revision was being queued — re-issue it";
                await store.UpsertRevisionAsync(doc, ct);
                return Results.Conflict(new { error = doc.Error });
            }

            // 202, not 200: the revision is queued and running, not applied. The operator polls the trail
            // below for its outcome.
            return Results.Accepted($"/projects/{projectId}/revisions",
                new { revisionId, status = RevisionStatus.Pending });
        });

        // The audit trail: every "change X because Y" the operator ever asked for, oldest first.
        app.MapGet("/projects/{projectId}/revisions",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
                Results.Json(await store.GetRevisionsAsync(projectId, ct), Json.Options));
    }
}
