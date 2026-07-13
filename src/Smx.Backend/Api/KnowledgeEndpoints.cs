using Microsoft.AspNetCore.Mvc;
using Smx.Domain;

namespace Smx.Backend.Api;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative: without it, minimal APIs infer whether `store`
        // is a service by checking IServiceProviderIsService at endpoint-build time. Test hosts that
        // don't register IKnowledgeStore (e.g. ProjectEndpointsTests, which only registers IRecordStore)
        // would then have it inferred as a request body, which is illegal on GET and throws while
        // building the composite endpoint data source — breaking routing for the ENTIRE app, not just
        // these routes.
        app.MapGet("/marker-library", async (string? search, [FromServices] IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryMarkersAsync(search, ct), Json.Options));

        app.MapGet("/learned-conclusions", async (string? search, [FromServices] IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryLearnedConclusionsAsync(search, ct), Json.Options));

        // /msds-registry (GET + review) added in Task 9.
    }
}
