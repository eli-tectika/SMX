using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every IRecordStore param below is required, not decorative: without it,
        // minimal APIs infer whether a param is a service via IServiceProviderIsService at endpoint-build
        // time (shared across the WHOLE app's composite endpoint data source). A test host that registers
        // only IKnowledgeStore (e.g. KnowledgeEndpointsTests) and not IRecordStore would otherwise fail to
        // build these routes, which breaks routing for every endpoint in the app, not just these.
        app.MapPost("/projects", async (CreateProjectRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (req.Validate() is { } error) return Results.BadRequest(new { error });
            var projectId = $"proj-{Guid.NewGuid():N}"[..17];
            // This payload is the ONLY thing intake reads (IntakeAgent copies the facts straight out of it),
            // so a field dropped here is a field no downstream stage can ever see. `device` is left to
            // Json.Options' WhenWritingNull when absent: no key at all, rather than a `null` masquerading as
            // a device.
            var payload = JsonSerializer.SerializeToElement(new
            {
                components = req.Components,
                elementPools = req.ElementPools,
                providedCandidates = req.Candidates ?? [],
                clientRestrictedList = req.ClientRestrictedList ?? [],
                measuredBackground = req.MeasuredBackground ?? [],
                device = req.Device,
            }, Json.Options);
            var doc = ProjectDoc.Create(projectId, req.Client, req.Product, payload);
            doc.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertProjectAsync(doc, ct);
            return Results.Accepted($"/projects/{projectId}", new { projectId });
        });

        // GET /projects lives in ProjectsListEndpoints, not here: it carries the gate statuses the
        // "Needs signing" card is computed from, so it reads gates as well as projects.

        // The payload is returned, not just the stage spine. It is the operator's OWN submitted input —
        // never an agent's output — so echoing it back cannot launder a fabricated verdict into the UI;
        // it is the safest data in the record to show. It is also the LIVE intake record rather than a
        // stale snapshot: record_answer gap-fills this very element (ChatTools.cs:227-230), and only
        // while constraints do not yet exist, after which it is frozen.
        //
        // Its shape is fixed by the anonymous object POST /projects builds — not a verbatim echo of the
        // request — so there is no unbounded key surface here.
        app.MapGet("/projects/{projectId}", async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetProjectAsync(projectId, ct) is { } doc
                ? Results.Json(new { doc.ProjectId, doc.Client, doc.Product, doc.Stages, doc.Payload }, Json.Options)
                : Results.NotFound());

        app.MapGet("/projects/{projectId}/matrix",
            async (string projectId, string? format, [FromServices] IRecordStore store, CancellationToken ct) =>
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
            async (string projectId, ReviewRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.EvidenceReviewed = true;
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { reviewed = true });
        });

        app.MapPost("/projects/{projectId}/regulatory/determination",
            async (string projectId, DeterminationRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            // Ordinal, exact, and the ONLY writer of VerdictDoc.Determination: "Recommended", " recommended "
            // and "approved" are all 422s, so the string CompliantSet reads is always one of the two constants.
            if (req.Determination is not (Determinations.Recommended or Determinations.Rejected))
                return Results.UnprocessableEntity(new { error = $"determination must be '{Determinations.Recommended}' or '{Determinations.Rejected}'" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "every determination requires a reason" });
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.Determination = req.Determination;
            v.DeterminationReason = req.Reason;
            v.EvidenceReviewed = true; // recording a ruling implies you reviewed the evidence
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { v.Determination });
        });

        app.MapPost("/projects/{projectId}/regulatory/approve",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            if (candidates is null || !MatrixAssembler.IsComplete(candidates, verdicts))
                return Results.UnprocessableEntity(new { error = "regulatory analysis incomplete — every candidate needs a verdict before sign-off" });
            // Arm on the LIVE analysis: a revise can leave an orphan verdict behind for a cell that is no
            // longer screened, and blocking on an item the operator cannot open would deadlock this gate.
            var (ok, blockers) = RegulatoryGate.Armable(candidates, verdicts);
            if (!ok)
                return Results.UnprocessableEntity(new { error = "gate not armable — open the flagged items first", blockers });
            var existing = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            await store.UpsertGateAsync(new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
                GateType = GateTypes.Regulatory, Status = "approved",
                ApprovedAt = existing?.Status == "approved" ? existing.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Ok(new { status = "approved" });
        });

        app.MapGet("/projects/{projectId}/gate/regulatory",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            var complete = candidates is not null && MatrixAssembler.IsComplete(candidates, verdicts);
            // No candidates ⇒ no live cells ⇒ nothing to have reviewed. `complete` already fails below, and
            // the "incomplete" blocker is the honest reason; inventing verdict blockers here would not be.
            var (armed, blockers) = candidates is null
                ? (Ok: false, Blockers: (IReadOnlyList<string>)[])
                : RegulatoryGate.Armable(candidates, verdicts);
            var armable = complete && armed;
            var allBlockers = complete ? blockers : blockers.Prepend("incomplete: not every candidate has a verdict yet").ToList();
            var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            return Results.Json(new
            {
                status = gate?.Status ?? "locked",
                armable,
                blockers = allBlockers,
                approvedAt = gate?.ApprovedAt,
            }, Json.Options);
        });

        // The per-stage reads (§7): thin projections mirroring GET /dosing — the doc verbatim or a 404.
        app.MapGet("/projects/{projectId}/candidates",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetCandidatesAsync(projectId, ct) is { } candidates
                ? Results.Json(candidates, Json.Options)
                : Results.NotFound());

        // A partition query, never a 404: an empty analysis is a state, not an error (mirror GetVerdictsAsync).
        app.MapGet("/projects/{projectId}/verdicts",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            Results.Json(await store.GetVerdictsAsync(projectId, ct), Json.Options));

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
    }
}
