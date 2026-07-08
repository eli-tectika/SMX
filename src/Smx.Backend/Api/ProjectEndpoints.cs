using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects", async (CreateProjectRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (req.Validate() is { } error) return Results.BadRequest(new { error });
            var projectId = $"proj-{Guid.NewGuid():N}"[..17];
            var payload = JsonSerializer.SerializeToElement(req, Json.Options);
            var doc = ProjectDoc.Create(projectId, req.Client, req.Product, payload);
            doc.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertProjectAsync(doc, ct);
            return Results.Accepted($"/projects/{projectId}", new { projectId });
        });

        app.MapGet("/projects/{projectId}", async (string projectId, IRecordStore store, CancellationToken ct) =>
            await store.GetProjectAsync(projectId, ct) is { } doc
                ? Results.Json(new { doc.ProjectId, doc.Client, doc.Product, doc.Stages }, Json.Options)
                : Results.NotFound());

        app.MapGet("/projects/{projectId}/matrix",
            async (string projectId, string? format, IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetMatrixAsync(projectId, ct) is not { } matrix) return Results.NotFound();
            if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.Json(matrix, Json.Options);
            var bytes = MatrixXlsxWriter.Write(matrix); // Task 6
            return Results.File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{projectId}-compatibility-matrix.xlsx");
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    }
}
