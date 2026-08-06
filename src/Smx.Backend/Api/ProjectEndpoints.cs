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
            async (string projectId, [FromServices] IRecordStore store,
                   [FromServices] PipelineRunner? runner, [FromServices] PipelineSupervisor? supervisor,
                   CancellationToken ct) =>
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
            // A re-POST by the same signer is idempotent; a POST over a machine or unattributed
            // signature is a NEW human signature and gets its own timestamp. The two fields move
            // together or the record can describe an event that never happened: an operator
            // signature stamped with the machine's timestamp, or a machine signature that
            // survives the human review that was supposed to replace it.
            var reaffirming = existing is { Status: "approved", ApprovedBy: GateSigners.Operator };
            var gate = new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
                GateType = GateTypes.Regulatory, Status = "approved",
                // This endpoint is only reachable by the operator pressing Sign — there is no agent
                // tool for it and there never will be.
                ApprovedAt = reaffirming ? existing!.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
                ApprovedBy = GateSigners.Operator,
            };
            await store.UpsertGateAsync(gate, ct);

            // The signature is recorded; now make it MEAN something. Two separate acts, deliberately kept
            // separate:
            //   OnGateAsync is a stamp — the Regulatory stage leaves `awaiting-RE`. It touches one record and
            //   runs no agent, so it is safe on the caller's thread.
            //   TryStart is what actually moves the project: Dosing SKIPS behind an unsigned gate, so before
            //   this line the signed determination changed nothing and the project sat parked under a gate
            //   the operator had already signed — waiting for a change feed that no longer exists.
            // The runner itself never re-enters the pipeline from a gate; re-entry is the supervisor's job,
            // and it happens here rather than inside OnGateAsync so that stamping a signature can never turn
            // into an endpoint that spends minutes in Foundry.
            if (runner is not null) await runner.OnGateAsync(gate, ct);
            supervisor?.TryStart(projectId);
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
            return Results.Json(new RegulatoryGateResponse
            {
                Status = gate?.Status ?? "locked",
                Armable = armable,
                Blockers = allBlockers,
                ApprovedAt = gate?.ApprovedAt,
                ApprovedBy = gate?.ApprovedBy,
            }, Json.Options);
        });

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

        // The need-driven pool (the agent-proposed candidate chemistries), or a 404 before the pool agent has
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

/// GET /gate/regulatory's shape. A named record, not an anonymous object, because ApprovedAt and
/// ApprovedBy must reach the client as `null` — present-and-null distinguishes "not yet approved"
/// from "approved, signer unknown" — and <see cref="Json.Options"/> sets `DefaultIgnoreCondition =
/// WhenWritingNull` globally, which would otherwise drop the key entirely and read as `undefined`.
internal sealed record RegulatoryGateResponse
{
    public required string Status { get; init; }
    public required bool Armable { get; init; }
    public required IReadOnlyList<string> Blockers { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ApprovedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ApprovedBy { get; init; }
}
