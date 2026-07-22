using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Documents;

namespace Smx.Backend.Api;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative — see the comment in KnowledgeEndpoints: without
        // it, minimal APIs may infer the service as a request body, which is illegal on GET and
        // breaks routing for the ENTIRE app while building the composite endpoint data source.
        app.MapGet("/documents", async (string? kind, string? q, string? state,
            [FromServices] IDocumentCatalog catalog, CancellationToken ct) =>
        {
            var filter = new DocumentFilter(
                Kind: kind is { Length: > 0 } ? kind : DocumentKinds.All,
                Q: q,
                State: state is { Length: > 0 } ? state : DocumentStates.All);
            return Results.Json(await catalog.ListAsync(filter, ct), Json.Options);
        });

        app.MapGet("/documents/{id}", async (string id, [FromServices] IDocumentCatalog catalog,
            CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            return Results.Json(ToWire(detail), Json.Options);
        });
    }

    /// The wire shape drops BlobPath. Returning it would hand the client the exact string the id
    /// scheme exists to keep out of the API surface.
    internal static object ToWire(DocumentDetail d) => new
    {
        summary = d.Summary,
        provenance = d.Provenance,
        unavailableReason = d.UnavailableReason,
        unavailableDetail = d.UnavailableDetail,
        supersededById = d.SupersededById,
    };
}
