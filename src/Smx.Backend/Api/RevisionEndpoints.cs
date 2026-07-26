using Microsoft.AspNetCore.Mvc;
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
            async (string projectId, string stage, ReviseRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
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

            var revisionId = RecordIds.Revision(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            await store.UpsertRevisionAsync(new RevisionDoc
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
            }, ct);

            // 202, not 200 — record-as-bus. The backend cannot run an agent, so WRITING THE DOC IS THE
            // DISPATCH: the orchestrator's change feed picks it up, re-runs the stage, voids the gate the
            // revision invalidates and writes the Learned Conclusion. Nothing has happened yet.
            return Results.Accepted($"/projects/{projectId}/revisions",
                new { revisionId, status = RevisionStatus.Pending });
        });

        // The audit trail: every "change X because Y" the operator ever asked for, oldest first.
        app.MapGet("/projects/{projectId}/revisions",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
                Results.Json(await store.GetRevisionsAsync(projectId, ct), Json.Options));
    }
}
