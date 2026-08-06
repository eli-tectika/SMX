using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class TableEndpoints
{
    /// GET /projects/{id}/table — the whole project record as one table (redesign spec §5.6).
    ///
    /// The join is done HERE, once, rather than in the client. Five endpoints returning five slices meant
    /// five chances for the UI and the XLSX export to disagree about what the record says; they now read the
    /// same projection.
    public static void MapTableEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/projects/{projectId}/table",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var project = await store.GetProjectAsync(projectId, ct);
            if (project is null) return Results.NotFound();

            // Every downstream record is OPTIONAL. A project that has only reached Discovery returns rows
            // carrying just that group — a 200 with rows, never a 404: an analysis in progress is a state,
            // not a missing resource, and 404 here would make the table unusable for exactly the projects
            // an operator most wants to watch.
            var rows = ProjectTable.Build(
                await store.GetCandidatesAsync(projectId, ct),
                await store.GetVerdictsAsync(projectId, ct),
                await store.GetDosingAsync(projectId, ct),
                await store.GetDecisionAsync(projectId, ct),
                project.Stages);

            return Results.Json(new { projectId, rows }, Json.Options);
        });
    }
}
