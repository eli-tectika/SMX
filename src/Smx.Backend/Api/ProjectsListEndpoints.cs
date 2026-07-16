using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// GET /projects — the estate list the frontend's landing page reads. Newest-first, each row carrying
/// the stage spine and both gate statuses: the "Needs signing" card is computed from the gates, and
/// nothing less than the record can be allowed to back it.
public static class ProjectsListEndpoints
{
    public static void MapProjectsListEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative — same reason as ProjectEndpoints: a test host that
        // never registers IRecordStore would otherwise fail to build this route, breaking routing app-wide.
        app.MapGet("/projects", async ([FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var projects = await store.GetProjectsAsync(ct: ct);
            var list = new List<object>(projects.Count);
            foreach (var p in projects)   // newest-first, straight from the store's ORDER BY
            {
                var regulatory = await store.GetGateAsync(p.ProjectId, GateTypes.Regulatory, ct);
                var vp = await store.GetGateAsync(p.ProjectId, GateTypes.Vp, ct);
                list.Add(new
                {
                    p.ProjectId, p.Client, p.Product, p.CreatedAt, p.Stages,
                    // A Dictionary, not an anonymous object: Json.Options drops null PROPERTIES
                    // (WhenWritingNull), but an absent gate must serialize as an EXPLICIT null —
                    // "no gate yet" is a value the frontend reads, not a field it has to infer —
                    // and dictionary entries are exempt from the ignore condition.
                    gates = new Dictionary<string, string?>
                    {
                        [GateTypes.Regulatory] = regulatory?.Status,
                        [GateTypes.Vp] = vp?.Status,
                    },
                });
            }
            return Results.Json(list, Json.Options);
        });
    }
}
