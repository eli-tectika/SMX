using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
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

        // GET /projects lives in ProjectsListEndpoints, not here: it carries the VP gate status the
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

            // The XLSX is the WIDE table now, not the matrix document — same projection the UI reads
            // (ProjectTable), so a spreadsheet handed to a customer cannot disagree with the screen it was
            // exported from. The JSON branch above still serves MatrixDoc verbatim for existing callers.
            var project = await store.GetProjectAsync(projectId, ct);
            if (project is null) return Results.NotFound();
            var rows = ProjectTable.Build(
                await store.GetCandidatesAsync(projectId, ct),
                await store.GetVerdictsAsync(projectId, ct),
                await store.GetDosingAsync(projectId, ct),
                await store.GetDecisionAsync(projectId, ct),
                project.Stages);
            var bytes = MatrixXlsxWriter.Write(rows);
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

        // The operator's OVERRIDE of the agent's proposal — and, since §16.4 dropped the regulatory gate,
        // the only thing an operator writes about a verdict besides "I opened it". It is no longer the
        // admission ticket (CompliantSet admits the agent's proposal by default); `rejected` here is a VETO
        // that nothing can rescue, and `recommended` overrules an agent's refusal. Both carry a reason.
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

        // POST /projects/{id}/regulatory/approve AND GET /projects/{id}/gate/regulatory LIVED HERE.
        //
        // Both are DELETED by the 2026-08-06 redesign §16.4 — the regulatory gate is dropped entirely, not
        // demoted: no GateDoc, no approve endpoint, no signature. The matrix carries confidence and sources
        // and is the review surface. Do not restore them without reading §16.4 and CompliantSet: the gate
        // was the writer of every operator determination, and its deletion is what forced the compliant set
        // to admit the agent's proposal by default.
        //
        // WHAT DID NOT GO WITH IT, so it is findable from here:
        //   - POST /regulatory/review and /determination, just above — the operator still opens items and
        //     still overrides the agent's proposal, and both still record a reason.
        //   - the arming predicate, now EvidenceReview.Outstanding — a precondition on POST /orders/{cas}
        //     and the VP signature rather than on a gate, because "no live flagged verdict is unopened"
        //     never needed a signature to mean something.
        //   - GET /projects/{id}/regulatory/compliance-package (ExportEndpoints), still ungated: it is the
        //     artifact that goes TO the R.E., so gating it on the R.E.'s answer was always backwards.

        // The operator's signature that the dossier is right. There is NO agent tool for this and there
        // never will be: creating a project is safe to delegate because it runs nothing, but starting
        // the analysis is the human asserting that what the agent wrote is correct (design §2.3).
        //
        // Writing `pending` is what makes the project runnable: PipelineRunner.RunIntakeAsync skips a
        // project still at `awaiting-confirmation`, so until this endpoint is called no pass over it can
        // start intake. That is why this endpoint, and not create_project, is the trigger — and the flip is
        // the precondition whether or not a supervisor is there to launch the runner.
        //
        // The supervisor is OPTIONAL here, unlike on §7.3's control endpoints. Those have no answer without
        // it; this one does — the readiness checks and the flip are the operator's signature, and they are
        // meaningful on their own. A test host that registers only an IRecordStore therefore still exercises
        // the door it cares about. Production always has one (BackendHost registers it beside the runner, and
        // BackendHostWiringTests fails if that stops being true), so the null branch never runs in Azure.
        app.MapPost("/projects/{projectId}/start",
            async (string projectId, [FromServices] IRecordStore store,
                   [FromServices] PipelineSupervisor? supervisor, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();

            // BEFORE the idempotent branch below, not after: §7.3 says a start against a live pipeline is a
            // 409, and a 202 carrying the current status would read to the client as "already fine" when what
            // it means is "something is running that you did not just start".
            if (supervisor?.IsRunning(projectId) == true)
                return Results.Conflict(new { error = "a pipeline is already running for this project" });

            var intake = project.Stages[Stages.Intake];
            // Idempotent, and not merely tolerant: everything in this system is at-least-once, and a
            // double-press must never re-dispatch a stage that has already run. An already-authorised
            // project simply re-enters the pipeline; the per-stage guards absorb everything that has run.
            if (project.AnalysisStartedAt is not null)
            {
                supervisor?.TryStart(projectId);
                return Results.Accepted($"/projects/{projectId}", new { projectId, status = intake.Status });
            }

            var payload = JsonSerializer.Deserialize<StartPreconditions>(project.Payload.GetRawText(), Json.Options);
            if (payload?.Components is not { Count: > 0 })
                return Results.UnprocessableEntity(new
                {
                    error = "this project has no components. Every stage downstream runs per component — " +
                            "ask the agent to propose the component breakdown before starting.",
                });
            if (payload.Components.FirstOrDefault(c => c.Markets is not { Count: > 0 }) is { } noMarkets)
                return Results.UnprocessableEntity(new
                {
                    error = $"component '{noMarkets.Id}' has no target markets, which would leave it with an " +
                            "EMPTY regulatory screen. Ask the agent to record its markets before starting.",
                });

            // THE SIGNATURE. It used to be a flip of the intake stage from `awaiting-confirmation` to
            // `pending`. Intake has already run by the time an operator gets here — it transcribes the
            // interview during project creation — so what this endpoint authorises is everything AFTER
            // intake, and RunAsync refuses to advance past intake while this is null.
            project.AnalysisStartedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertProjectAsync(project, ct);

            // AND NOW IT ACTUALLY RUNS. The stamp alone used to be the whole endpoint, back when a change
            // feed was watching the record; nothing watches it any more, so a start that only wrote the
            // timestamp would leave the project sitting there looking started and never move.
            if (supervisor?.TryStart(projectId) == false)
                return Results.Conflict(new { error = "a pipeline is already running for this project" });
            return Results.Accepted($"/projects/{projectId}", new { projectId, status = intake.Status });
        });

        // The per-stage reads (§7): thin projections mirroring GET /dosing — the doc verbatim or a 404.
        app.MapGet("/projects/{projectId}/candidates",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetCandidatesAsync(projectId, ct) is { } candidates
                ? Results.Json(candidates, Json.Options)
                : Results.NotFound());

        // The need-driven pool (the agent-proposed candidate chemistries), or a 404 before the pool pass has
        // run. Read-only, like /candidates — the pool is derived data the operator inspects, not edits.
        app.MapGet("/projects/{projectId}/pool",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetPoolAsync(projectId, ct) is { } pool
                ? Results.Json(pool, Json.Options)
                : Results.NotFound());

        // A partition query, never a 404: an empty analysis is a state, not an error (mirror GetVerdictsAsync).
        app.MapGet("/projects/{projectId}/verdicts",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            Results.Json(await store.GetVerdictsAsync(projectId, ct), Json.Options));

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
    }
}

/// Just the slice of the payload /start checks. A dedicated shape rather than reusing the orchestrator's
/// IntakePayload, which is internal to that assembly and carries the physicist's data this must not read.
internal sealed class StartPreconditions
{
    public List<ComponentSpec> Components { get; set; } = [];
}

// RegulatoryGateResponse lived here, and went with GET /gate/regulatory. VpGateResponse in
// DecisionEndpoints.cs is now the only gate shape on the wire; its comment carries the present-and-null
// reasoning both records shared.
