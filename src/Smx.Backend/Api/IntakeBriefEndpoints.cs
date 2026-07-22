using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// What the operator reads when they open a project the interview agent created, and the catalogue the
/// interview screen measures coverage against.
public static class IntakeBriefEndpoints
{
    public static void MapIntakeBriefEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the
        // top of ProjectEndpoints. Without it, minimal APIs mis-infer it as a body param and break
        // routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/intake-brief", async (
            string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetIntakeBriefAsync(projectId, ct) is { } brief
                ? Results.Json(brief, Json.Options)
                // A project created through the old form has no brief. NOT an error — the intake screen
                // renders the record it does have and says plainly that there was no interview.
                : Results.NotFound());

        // The catalogue, served rather than duplicated. The frontend's coverage line and its
        // client-side gate mirror both need the full question set; a hard-coded TypeScript copy would
        // drift from this one the first time a question is added, and the screen would then under-report
        // what the analysis is missing. Static, cacheable, and takes no store.
        app.MapGet("/intake-questions", () => Results.Json(IntakeQuestions.All, Json.Options));
    }
}
