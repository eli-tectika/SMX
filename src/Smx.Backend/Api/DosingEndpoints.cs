using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The operator entering the one number that lives in no catalog (the metal loading), and the SOFT
/// code-finalization checkpoint. Neither is a gate: the loading write RE-OPENS Dosing (the ProjectDoc
/// upsert is the change-feed re-trigger), and the review is a note that blocks nothing (DosingDoc XML doc).
public static class DosingEndpoints
{
    public static void MapDosingEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the note in ProjectEndpoints:
        // minimal APIs infer service-vs-body params app-wide at endpoint-build time, so a missing attribute
        // breaks routing for the WHOLE app.
        app.MapPost("/projects/{projectId}/dosing/loading",
            async (string projectId, LoadingRequest req,
                   [FromServices] IRecordStore store, [FromServices] IKnowledgeStore knowledge, CancellationToken ct) =>
        {
            // Early, friendly-error guard. The hard safety net is OrderAmount.Compute (a bad loading on a
            // purchase order is the real harm); SubstancePropertyDoc.MetalLoading is deliberately unvalidated,
            // so this endpoint is the one place a bad operator entry is refused BEFORE it reaches the store.
            // 0 → an infinite order amount; >1 → more metal than compound → a silent under-order.
            if (req.MetalLoading is not (> 0 and <= 1))
                return Results.UnprocessableEntity(new { error = "metalLoading must be a mass fraction in (0, 1]" });
            // An unsourced number in the cross-project knowledge layer is worse than none: every future project
            // inherits it and nobody can check it. Basis is the operator's own words that make it checkable.
            if (string.IsNullOrWhiteSpace(req.Basis))
                return Results.UnprocessableEntity(new { error = "a metal loading requires a basis — the source that makes it checkable" });

            // Existence check FIRST: a 4xx must mean "nothing happened". Ordering this above the knowledge
            // write keeps a bad/stale projectId from committing a permanent cross-project write (and
            // re-stamping EnteredAt on retry) before it 404s — provenance in this layer must stay checkable.
            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();

            await knowledge.UpsertSubstancePropertyAsync(new SubstancePropertyDoc
            {
                Id = KnowledgeIds.SubstanceProperty(req.Cas),
                Cas = req.Cas,
                Element = req.Element,
                Form = req.Form,
                MetalLoading = req.MetalLoading,
                Basis = req.Basis,
                EnteredAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            // Re-trigger Dosing. The upsert IS the change-feed re-trigger — production's change feed re-runs
            // Dosing off this write; we do NOT run it here.
            project.Stages[Stages.Dosing].Status = "pending";
            await store.UpsertProjectAsync(project, ct);
            return Results.Accepted($"/projects/{projectId}/dosing", new { status = "pending" });
        });

        app.MapPost("/projects/{projectId}/dosing/review",
            async (string projectId, DosingReviewRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            // SOFT checkpoint (UX §4.5): records that the code-finalization review happened. It blocks nothing
            // and unlocks nothing — no stage status, no gate is touched here, by design.
            if (string.IsNullOrWhiteSpace(req.Note))
                return Results.UnprocessableEntity(new { error = "a review note is required — the checkpoint records what was reviewed" });
            if (await store.GetDosingAsync(projectId, ct) is not { } dosing) return Results.NotFound();
            dosing.ReviewNote = req.Note;
            dosing.ReviewedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertDosingAsync(dosing, ct);
            return Results.Accepted($"/projects/{projectId}/dosing", new { reviewed = true });
        });

        app.MapGet("/projects/{projectId}/dosing",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetDosingAsync(projectId, ct) is { } dosing
                ? Results.Json(dosing, Json.Options)
                : Results.NotFound());
    }
}

/// The operator entering a compound's metal loading (mass fraction of the marker element), with the
/// BASIS that makes it checkable. Written to the cross-project knowledge layer, keyed by CAS.
public sealed record LoadingRequest(string Cas, string Element, string Form, double MetalLoading, string Basis);

/// The soft code-finalization review note. Not a gate — see DosingDoc.ReviewNote.
public sealed record DosingReviewRequest(string Note);
