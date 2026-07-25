using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Xrf;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Xrf;

namespace Smx.Backend.Api;

/// The physicist's measured XRF result, entered by the operator.
///
/// Removing the creation form removed the only way this data could reach the record. It does NOT come
/// back through chat: `IntakeAnswers` refuses element pools by name, because a model transcribing
/// measured numbers is a mechanism by which a shaved background ships a marker under the detection
/// floor that nobody can read in the field.
///
/// Two endpoints, one of which writes. `parse` is pure — it reads a file and hands back proposals,
/// touching nothing. `confirm` is the single writer. Keeping them separate is what makes the
/// operator's confirmation a real act rather than a consequence of choosing a file.
public sealed record XrfConfirmRequest(List<XrfProposal> Proposals);

public static class XrfEndpoints
{
    public static void MapXrfEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at
        // the top of ProjectEndpoints. Without it, minimal APIs mis-infer it as a body param and break
        // routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/xrf", async (
            string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetConstraintsAsync(projectId, ct) is { } c
                ? Results.Json(new
                {
                    components = c.Components.Select(x => x.Id),
                    elementPools = c.ElementPools,
                    measuredBackgrounds = c.MeasuredBackgrounds,
                    device = c.Device,
                }, Json.Options)
                : Results.NotFound());

        app.MapPost("/projects/{projectId}/xrf/parse", async (
            string projectId, IFormFile file,
            [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.UnprocessableEntity(new { error = "no file was uploaded" });
            if (await store.GetConstraintsAsync(projectId, ct) is null) return Results.NotFound();

            IReadOnlyList<IReadOnlyList<string>> rows;
            try
            {
                await using var stream = file.OpenReadStream();
                rows = await XrfReaders.ReadAsync(file.FileName, stream, ct);
            }
            catch (XrfFormatException e)
            {
                // A file we cannot open is the operator's problem to fix, not a server error. A 500
                // here would read as "the system is broken" when the answer is "save it as .csv".
                return Results.UnprocessableEntity(new { error = e.Message });
            }

            var result = XrfSheet.Parse(rows);
            return result.SheetProblems.Count > 0
                ? Results.UnprocessableEntity(new { error = string.Join(" ", result.SheetProblems) })
                : Results.Json(result, Json.Options);
        }).DisableAntiforgery(); // multipart in a .NET 8 minimal API; see AttachmentEndpoints.

        app.MapPost("/projects/{projectId}/xrf/confirm", async (
            string projectId, XrfConfirmRequest req,
            [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetConstraintsAsync(projectId, ct) is not { } constraints)
                return Results.NotFound();

            // System.Text.Json binds a missing JSON property on a record constructor parameter to that
            // parameter's default — for `IReadOnlyList<string> Problems`, that is null, not []. Left
            // alone that becomes an NRE inside XrfConfirmation.Build's `p.Problems.Count` checks for
            // any caller that omits an empty array rather than sending one explicitly. Normalise here,
            // at the door, rather than weakening XrfConfirmation's contract to tolerate a null it should
            // never have to consider.
            var proposals = req.Proposals.Select(p => p.Problems is null ? p with { Problems = [] } : p).ToList();

            var (built, error) = XrfConfirmation.Build(
                proposals, [.. constraints.Components.Select(c => c.Id)]);
            if (error is not null) return Results.UnprocessableEntity(new { error });

            // REPLACE, never append. A re-measure is a correction: appending would leave two
            // measurements of the same element in the record, which DetectionFloor then refuses to
            // compute a floor from — so the operator's fix would break dosing instead of repairing it.
            constraints.ElementPools = [.. built!.ElementPools];
            constraints.MeasuredBackgrounds = [.. built.MeasuredBackgrounds];
            if (built.Device is not null) constraints.Device = built.Device;

            // This write IS the dispatch. The change feed picks up the constraints document and the
            // orchestrator runs Discovery — which, until this moment, was parked precisely because
            // there were no pools to screen against.
            await store.UpsertConstraintsAsync(constraints, ct);

            return Results.Accepted($"/projects/{projectId}", new
            {
                projectId,
                pools = built.ElementPools.Count,
                backgrounds = built.MeasuredBackgrounds.Count,
                device = built.Device?.Model,
            });
        });

        // Served from the same constant the parser reads, so the template cannot drift from what the
        // parser accepts.
        app.MapGet("/xrf-template.csv", () => Results.Text(XrfTemplate.Csv, "text/csv"));
    }
}
