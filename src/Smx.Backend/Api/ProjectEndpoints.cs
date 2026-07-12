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
            var payload = JsonSerializer.SerializeToElement(new
            {
                components = req.Components,
                elementPools = req.ElementPools,
                providedCandidates = req.Candidates ?? [],
                clientRestrictedList = req.ClientRestrictedList ?? [],
            }, Json.Options);
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

        app.MapPost("/projects/{projectId}/regulatory/review",
            async (string projectId, ReviewRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.EvidenceReviewed = true;
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { reviewed = true });
        });

        app.MapPost("/projects/{projectId}/regulatory/determination",
            async (string projectId, DeterminationRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (req.Determination is not ("recommended" or "rejected"))
                return Results.UnprocessableEntity(new { error = "determination must be 'recommended' or 'rejected'" });
            if (req.Determination == "rejected" && string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "a rejected determination requires a reason" });
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.Determination = req.Determination;
            v.DeterminationReason = req.Reason;
            v.EvidenceReviewed = true; // recording a ruling implies you reviewed the evidence
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { v.Determination });
        });

        app.MapPost("/projects/{projectId}/regulatory/approve",
            async (string projectId, IRecordStore store, CancellationToken ct) =>
        {
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            if (candidates is null || !MatrixAssembler.IsComplete(candidates, verdicts))
                return Results.UnprocessableEntity(new { error = "regulatory analysis incomplete — every candidate needs a verdict before sign-off" });
            var (ok, blockers) = RegulatoryGate.Armable(verdicts);
            if (!ok)
                return Results.UnprocessableEntity(new { error = "gate not armable — open the flagged items first", blockers });
            await store.UpsertGateAsync(new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
                GateType = GateTypes.Regulatory, Status = "approved",
                ApprovedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Ok(new { status = "approved" });
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    }
}
