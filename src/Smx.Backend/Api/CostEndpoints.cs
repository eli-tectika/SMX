using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The read surface over the deterministic supplier-price audit (§3.4). Cost holds no agent — it is a
/// catalog lookup and a price parse — so there is nothing here to write or sign: the stage runs on the bus
/// and this endpoint simply returns the CostDoc it persisted, every figure carrying its citation.
public static class CostEndpoints
{
    public static void MapCostEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on the store param is required, not decorative — see the note in ProjectEndpoints:
        // minimal APIs infer service-vs-body params app-wide at endpoint-build time, so a missing attribute
        // breaks routing for the WHOLE app.
        app.MapGet("/projects/{projectId}/cost",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetCostAsync(projectId, ct) is { } cost
                ? Results.Json(cost, Json.Options)
                : Results.NotFound());
    }
}
