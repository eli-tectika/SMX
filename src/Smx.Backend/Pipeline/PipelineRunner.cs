using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Backend.Agents;
using Smx.Backend.Cost;
using Smx.Backend.Knowledge;

namespace Smx.Backend.Pipeline;

/// One project's run, start to finish, as plain sequential code.
///
/// This replaces StageDispatcher and the change feed. What that model bought — decoupled stages — it
/// paid for in at-least-once idempotency bookkeeping on every branch, and it did NOT buy durable
/// dispatch: the dispatcher's own comments recorded three times that a crash checkpoints and loses.
/// Here, resume is the skip-if-output-exists check in each stage body, and it is the same check that
/// makes the happy path idempotent, so there is one rule instead of nine.
///
/// What is NOT the pipeline, and lives here only because it is the same body of logic: the three
/// operator-triggered entry points — <see cref="OnGateAsync"/> (a signature), <see cref="OnRevisionAsync"/>
/// (revise-with-reason) and <see cref="OnChatMessageAsync"/> (a chat turn). They are called by endpoints,
/// not by <see cref="RunAsync"/>.
///
/// <paramref name="knowledge"/> is an OPTIONAL trailing parameter deliberately: it is read only by the
/// Dosing path (metal loadings live in the cross-project knowledge layer, not on the per-project bus).
/// When it is null, Dosing treats every loading as unknown and parks in `awaiting-operator` rather than
/// guessing: it degrades safely.
///
/// <paramref name="catalog"/> is OPTIONAL for the SAME reason: it is read only by the Cost path. When it
/// is null Cost skips entirely, never fabricating an audit from an absent catalog.
public sealed class PipelineRunner(
    IRecordStore store,
    IRunStore runs,
    IAgentRuns agents,
    ThreadEventHub hub,
    ILearnedConclusionWriter conclusions,
    int regulatoryParallelism,
    ILogger<PipelineRunner>? logger = null,
    IKnowledgeStore? knowledge = null,
    ICatalogLookup? catalog = null)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _live = new();

    /// Cancel one in-flight run. Returns false when the run is not live here — which on a single
    /// merged service means it is not live at all.
    public bool CancelRun(string runId) =>
        _live.TryGetValue(runId, out var cts) && Try(() => cts.Cancel());

    private static bool Try(Action act) { act(); return true; }

    public async Task RunAsync(string projectId, CancellationToken hostToken)
    {
        // Ordered exactly as the journey is (spec §3.1). `background` is still the pass-through it was;
        // `matrix`, `cost` and the decision assembly are deterministic and get runs with a null agent so
        // the operator can tell arithmetic from reasoning.
        var stages = new (string Stage, StageBody Run)[]
        {
            (Stages.Intake,     RunIntakeAsync),
            (Stages.Pool,       RunPoolAsync),
            (Stages.Background, RunBackgroundAsync),
            (Stages.Discovery,  RunDiscoveryAsync),
            (Stages.Regulatory, RunRegulatoryAsync),
            (Stages.Matrix,     RunMatrixAsync),
            (Stages.Dosing,     RunDosingAsync),
            (Stages.Cost,       RunCostAsync),
            (Stages.Decision,   RunDecisionAsync),
        };

        foreach (var (stage, run) in stages)
        {
            var outcome = await ExecuteAsync(projectId, stage, run, hostToken);
            // Anything but `done` stops the pipeline. Carrying on would run the next stage over an input
            // that does not exist, and produce a confident answer built on a hole.
            if (outcome != RunOutcome.Done) return;
        }
    }

    /// A stage body. It is handed the run's trail and decides for itself whether it has work: it returns
    /// Skip() before writing anything, or writes its `started` step and proceeds.
    ///
    /// ONE invocation, deliberately. An earlier shape called the body twice — once to "probe" for the skip
    /// and once to run — which read the record twice and used `ct == CancellationToken.None` as a hidden
    /// mode flag. Two calls means two chances to disagree about whether there is work.
    private delegate Task<StageResult> StageBody(RunTrail trail, CancellationToken ct);

    /// What a stage body returns once it has run. A skipped stage returns Skip() and never gets here.
    ///
    /// <param name="StageStatus">the status to stamp on the STAGE, when it differs from the run's own
    /// outcome. The two are not the same thing: a Regulatory run that produced every verdict is `done`
    /// as a RUN and `awaiting-RE` as a STAGE, because the R.E. has not signed yet; a Decision run that
    /// produced a proposal parks at `awaiting-VP`. Null ⇒ the stage takes the run's outcome.</param>
    public sealed record StageResult(
        string Outcome, string? Error, string? Summary, string? RecordId, string? StageStatus = null);

    /// Nothing to do — the stage's output is already on file, or its precondition is absent. The trail was
    /// never opened, so no empty group appears in the timeline.
    public static StageResult Skip() => new(RunOutcome.Done, null, null, null);

    /// The one place a run is opened, stamped and closed. Every stage body goes through it, so the trail
    /// cannot diverge between stages and neither can the cancel semantics.
    private async Task<string> ExecuteAsync(
        string projectId, string stage, StageBody body, CancellationToken hostToken)
    {
        var ordinal = (await runs.ListAsync(projectId, stage, hostToken)).Count + 1;
        var doc = new RunDoc
        {
            Id = RunIds.Run(projectId, stage, ordinal),
            ProjectId = projectId,
            Stage = stage,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _live[doc.Id] = cts;
        var trail = new RunTrail(doc, runs, hub, logger);

        try
        {
            var result = await body(trail, cts.Token);

            // The body wrote nothing ⇒ it skipped. No run doc was persisted and no stage status moves: a
            // stage that was already done stays done, which is what makes resume silent.
            if (!trail.Opened) return RunOutcome.Done;

            if (result.Summary is { } summary)
                await trail.StepAsync(RunStepKind.Output, summary,
                    new RunStepDetail { RecordId = result.RecordId }, cts.Token);

            await trail.StepAsync(RunStepKind.Outcome, Sentence(result.Outcome, result.Error), ct: cts.Token);
            await trail.CompleteAsync(result.Outcome, result.Error, hostToken);
            await StampAsync(projectId, stage, result.StageStatus ?? result.Outcome, result.Error, hostToken);
            return result.Outcome;
        }
        // The distinction the design calls out (§3.3): these arrive at the same catch and mean opposite
        // things. An operator cancel is a decision to record; a host shutdown must leave the stage
        // resumable, so it is re-thrown and the stage keeps its `running` status for the supervisor.
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !hostToken.IsCancellationRequested)
        {
            await trail.CompleteAsync(RunOutcome.Cancelled, "cancelled by the operator", CancellationToken.None);
            await StampAsync(projectId, stage, "needs-review", "cancelled by the operator", CancellationToken.None);
            return RunOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            await trail.CompleteAsync(RunOutcome.Failed, e.Message, hostToken);
            await StampAsync(projectId, stage, "failed", e.Message, hostToken);
            return RunOutcome.Failed;
        }
        finally
        {
            _live.TryRemove(doc.Id, out _);
        }
    }

    /// The stage's terminal stamp, and the ONE place `Attempts` moves. It counts entries into the stage,
    /// not successes — the dispatcher incremented it beside the `running` stamp for exactly that reason,
    /// and a failure that did not count would make a stage that has failed three times read as untried.
    private Task StampAsync(string projectId, string stage, string status, string? error, CancellationToken ct) =>
        SetStageAsync(projectId, stage, s => { s.Attempts++; s.Status = status; s.Error = error; }, ct);

    private static string Sentence(string outcome, string? error) => outcome switch
    {
        RunOutcome.Done => "Done.",
        RunOutcome.NeedsReview => $"Needs review: {error}",
        _ => error ?? outcome,
    };

    /// Has this stage already produced its output?
    ///
    /// For Intake, Pool and Discovery the answer is "is the output doc on file" — those docs are written
    /// once and nothing invalidates them in place. For the four DOWNSTREAM stages it is the STAGE STATUS,
    /// and that is not a stylistic choice: the dispatcher guarded them on status deliberately, and two
    /// tests pin why. (1) The soft code-finalization checkpoint upserts the SAME DosingDoc to record a
    /// review note — under a doc-existence guard that note re-prices the whole project. (2) A revision
    /// that replaces an upstream record resets Cost/Decision/Matrix to `pending`; under a doc-existence
    /// guard the stale artifact would simply be skipped over, and a compliance artifact that is wrong and
    /// looks current is the single most dangerous thing this system can produce.
    ///
    /// `running` counts as NOT run: it means a process died holding the stage, and re-entering is exactly
    /// the resume the design asks for. Nothing else re-enters — `done`, the parks, `failed` and
    /// `needs-review` are states a human or an explicit retry moves out of, never this pass.
    private async Task<bool> HasRunAsync(string projectId, string stage, CancellationToken ct) =>
        (await store.GetProjectAsync(projectId, ct))?.Stages.GetValueOrDefault(stage)?.Status
            is not (null or StageStatus.Pending or StageStatus.Running);

    // ---------------------------------------------------------------------------------------------
    // The stages.
    // ---------------------------------------------------------------------------------------------

    private async Task<StageResult> RunIntakeAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        // BOTH guards, as the dispatcher had. The status one is not bookkeeping: an interview-created
        // project sits at `awaiting-confirmation` until the operator presses Start Processing, and
        // `awaiting-confirmation` is emphatically "has run" as far as this pass is concerned. The agent
        // may create a project; only the operator may start one (design §2.3), and running intake off a
        // doc-existence check alone would be the runner making that call for them.
        if (await HasRunAsync(projectId, Stages.Intake, ct)) return Skip();
        if (await store.GetConstraintsAsync(projectId, ct) is not null) return Skip();
        var project = await LoadProjectAsync(projectId, ct);

        trail.Run.Agent = IntakeAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Reading the intake brief for {project.Client} / {project.Product}.", ct: ct);
        await SetStageAsync(projectId, Stages.Intake, s => s.Status = "running", ct);

        var result = await agents.RunIntakeAsync(project, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        var constraints = result.Output!;
        await store.UpsertConstraintsAsync(constraints, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Recorded {constraints.Components.Count} components, " +
            $"{constraints.Components.SelectMany(k => k.Markets).Distinct().Count()} target markets.",
            RecordIds.Constraints(projectId));
    }

    private async Task<StageResult> RunPoolAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await store.GetPoolAsync(projectId, ct) is not null) return Skip();
        // OnConstraintsAsync's top guard, kept: a project that already has a candidate set got one somehow,
        // and proposing a pool to feed a Discovery run that will not happen is work nobody asked for.
        if (await store.GetCandidatesAsync(projectId, ct) is not null) return Skip();
        if (await store.GetConstraintsAsync(projectId, ct) is not { } c) return Skip();
        // The need-only condition, from OnConstraintsAsync: an operator/eval pool or provided candidates
        // mean the pool agent has nothing to propose.
        if (c.ProvidedCandidates.Count > 0 || c.ElementPools.Count > 0) return Skip();

        // The first write. It opens the run — everything above this line could still skip.
        trail.Run.Agent = PoolAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Proposing a marker pool for {c.Components.Count} components: " +
            string.Join(", ", c.Components.Select(k => $"{k.Id} ({k.Material})")) + ".", ct: ct);
        await SetStageAsync(projectId, Stages.Pool, s => s.Status = "running", ct);

        var project = await LoadProjectAsync(projectId, ct);
        var result = await agents.RunPoolAsync(project, c, null, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        var pool = result.Output!;
        await store.UpsertPoolAsync(pool, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Proposed {pool.Suggestions.Count} markers across " +
            $"{pool.Suggestions.Select(s => s.Component).Distinct().Count()} components — " +
            string.Join(", ", pool.Suggestions.Take(3).Select(s => $"{s.Element}/{s.FormClass}")) + "…",
            RecordIds.Pool(projectId));
    }

    /// Background is the XRF filter — DEFERRED — so it is the PASS-THROUGH OnPoolAsync performed: mark it
    /// done and move on. It is the one body that writes nothing to the trail and still has an effect; a
    /// run group for a stage that did no work would be an empty box in the operator's timeline. When XRF
    /// is built, its filter goes HERE, before Discovery.
    private async Task<StageResult> RunBackgroundAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        // Guarded on the constraints, as OnPoolAsync was: a project with no intake output has no
        // background to have filtered, and stamping `done` there would advertise work over nothing.
        if (await store.GetConstraintsAsync(projectId, ct) is null) return Skip();
        await SetStageAsync(projectId, Stages.Background, s => { s.Status = "done"; s.Error = null; }, ct);
        return Skip();
    }

    private async Task<StageResult> RunDiscoveryAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await store.GetCandidatesAsync(projectId, ct) is not null) return Skip();
        if (await store.GetConstraintsAsync(projectId, ct) is not { } c) return Skip();

        // Known-candidate mode: bypass the Discovery agent when the operator/eval supplied candidates.
        if (c.ProvidedCandidates.Count > 0)
        {
            await trail.StepAsync(RunStepKind.Started,
                $"Recording {c.ProvidedCandidates.Count} operator-provided candidates — no agent runs.",
                ct: ct);
            await SetStageAsync(projectId, Stages.Discovery, s => s.Status = "running", ct);

            // This is the ONE door into the record that no agent validates. DiscoveryAgent.Validate
            // check-digits every CAS a model proposes, but these candidates never reach it — they land in
            // the CandidatesDoc verbatim and carry exactly the authority of a candidate an agent cited.
            // From here a wrong CAS flows into the regulatory screen, into dosing (against the wrong
            // molecular weight) and into procurement. A check digit makes a transposed digit PROVABLY
            // wrong, so refuse it at the door.
            //
            // Only the CAS is re-checked. Validate's other rails (the web-evidence Tier/preferred ceiling)
            // are about a MODEL's claims; these candidates come from the operator or an eval fixture, so a
            // hallucinated tier is not the failure mode here. A mistyped CAS is.
            var invalid = c.ProvidedCandidates.Where(s => !CasNumber.IsValid(s.Cas)).ToList();
            if (invalid.Count > 0)
            {
                var named = string.Join("; ", invalid.Select(s => $"'{s.Element}/{s.Form}' has CAS '{s.Cas}'"));
                return new StageResult(RunOutcome.NeedsReview,
                    $"provided candidate {named} — which fails its CAS check digit. " +
                    "Correct the CAS against a primary source and re-submit; it is not safe to " +
                    "screen, dose or order a substance identified by a CAS that is provably wrong.",
                    null, null);
            }

            await store.UpsertCandidatesAsync(new CandidatesDoc
            {
                Id = RecordIds.Candidates(projectId), ProjectId = projectId,
                Substances = [.. c.ProvidedCandidates],
            }, ct);
            return new StageResult(RunOutcome.Done, null,
                $"Recorded {c.ProvidedCandidates.Count} provided candidates.",
                RecordIds.Candidates(projectId));
        }

        // Discovery reads its element pool from ConstraintsDoc.ElementPools, so an agent-proposed pool is
        // mapped onto an IN-MEMORY copy of the constraints (never persisted — the stored ConstraintsDoc
        // stays the frozen operator input). That is what lets DiscoveryAgent and its rails stay untouched:
        // from Discovery's point of view an agent-proposed pool and an operator-entered one are the same
        // shape.
        if (c.ElementPools.Count == 0)
        {
            if (await store.GetPoolAsync(projectId, ct) is not { } pool) return Skip();
            c.ElementPools = PoolElementPools(pool); // IN-MEMORY only — do not persist
        }

        trail.Run.Agent = DiscoveryAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Finding candidate substances for {c.ElementPools.Select(e => e.Element).Distinct().Count()} " +
            $"elements across {c.ElementPools.Select(e => e.Component).Distinct().Count()} components.", ct: ct);
        await SetStageAsync(projectId, Stages.Discovery, s => s.Status = "running", ct);

        // The project carries the sensitive terms Discovery's web tool must refuse to send.
        var project = await LoadProjectAsync(projectId, ct);
        var result = await agents.RunDiscoveryAsync(project, c, null, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        var candidates = result.Output!;
        await store.UpsertCandidatesAsync(candidates, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Proposed {candidates.Substances.Count} candidates — " +
            $"{candidates.Substances.Count(s => s.Tier == "A")} Tier A, " +
            $"{candidates.Substances.Count(s => s.Tier == "B")} Tier B.",
            RecordIds.Candidates(projectId));
    }

    /// The pool → element-pool mapping Discovery consumes. Discovery needs only (component, element); the
    /// form-class hint stays in the PoolDoc (provenance + future Background use) and is deliberately NOT
    /// threaded into Discovery's own form selection for now. Line defaults to "Kα", status "V"
    /// (provisional — XRF deferred, so nothing is conditional yet). DISTINCT on (component, element): the
    /// pool may propose several forms of one element, but the element pool is a set of elements.
    private static List<ElementPool> PoolElementPools(PoolDoc pool) =>
        [.. pool.Suggestions
            .Select(s => (s.Component, s.Element))
            .Distinct()
            .Select(k => new ElementPool(k.Component, k.Element, "Kα", "V"))];

    /// Regulatory stays PARALLEL — it is the one stage where serial execution is a real wall-clock
    /// regression, and the operator's whole complaint is about waiting. Task 7 turns the fan-out into a
    /// parent run with one child per substance.
    private async Task<StageResult> RunRegulatoryAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return Skip();

        var existing = (await store.GetVerdictsAsync(projectId, ct))
            .Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = candidates.Substances
            .Where(s => s.Tier != "C" && !existing.Contains((s.Cas, s.ComponentId))).ToList();
        if (missing.Count == 0) return Skip();

        trail.Run.Agent = RegulatoryAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Screening {missing.Count} substances against " +
            $"{constraints.Components.SelectMany(k => k.Markets).Distinct().Count()} target markets.", ct: ct);
        await SetStageAsync(projectId, Stages.Regulatory, s => s.Status = "running", ct);

        var parentId = trail.Run.Id;
        using var gate = new SemaphoreSlim(regulatoryParallelism);
        var flagged = 0;

        await Task.WhenAll(missing.Select(async candidate =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Its OWN RunDoc and its own trail: RunTrail is a single-writer type, and the branches
                // run concurrently under Task.WhenAll.
                var child = new RunDoc
                {
                    Id = $"{parentId}|{candidate.Cas}|{candidate.ComponentId}",
                    ProjectId = projectId,
                    Stage = Stages.Regulatory,
                    StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Agent = RegulatoryAgent.AgentName,
                    Subject = $"{candidate.Cas}|{candidate.ComponentId}",
                    ParentRunId = parentId,
                };
                var childTrail = new RunTrail(child, runs, hub, logger);
                await childTrail.StepAsync(RunStepKind.Started,
                    $"Screening {candidate.Element}/{candidate.Form} (CAS {candidate.Cas}) for {candidate.ComponentId}.",
                    ct: ct);

                var result = await agents.RunRegulatoryAsync(constraints, candidate, null, childTrail, ct);
                // The needs-review VerdictDoc the dispatcher synthesised on failure is kept verbatim: an
                // absent verdict and a verdict that says "no cited verdict could be produced" are very
                // different things downstream, and only the second one blocks the gate honestly.
                var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                {
                    Id = RecordIds.Verdict(projectId, candidate.Cas, candidate.ComponentId),
                    ProjectId = projectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
                    Element = candidate.Element, Form = candidate.Form,
                    Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [], 0,
                        $"agent could not produce a valid cited verdict: {result.Error}")],
                };
                if (!result.Succeeded) Interlocked.Increment(ref flagged);
                await store.UpsertVerdictAsync(verdict, ct);

                await childTrail.StepAsync(RunStepKind.Output,
                    $"Verdict for {candidate.Cas} — {verdict.Dimensions[0].Status}.",
                    new RunStepDetail { RecordId = verdict.Id }, ct);
                await childTrail.CompleteAsync(
                    result.Succeeded ? RunOutcome.Done : RunOutcome.NeedsReview, result.Error, ct);
            }
            finally { gate.Release(); }
        }));

        // The RUN is done — every substance was screened. The STAGE is not: the R.E. has not signed, and
        // a Regulatory stage that reached `done` off its own agent's output would be the agent signing a
        // hard gate (Law 9). RunMatrixAsync computes which of the two it is, from the gate record.
        return new StageResult(RunOutcome.Done, null,
            $"Wrote {missing.Count} verdicts — {missing.Count - flagged} screened, {flagged} flagged.",
            null, StageStatus.AwaitingRe);
    }

    /// The compatibility matrix: a DETERMINISTIC fold over (candidates, verdicts). No agent — the null
    /// `trail.Run.Agent` is what tells the operator this stage is arithmetic rather than reasoning.
    ///
    /// It also owns the Regulatory stage's final status, because that status is a function of the SAME
    /// inputs: the gate record plus whether the current analysis is still covered by it.
    private async Task<StageResult> RunMatrixAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return Skip();
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(candidates, verdicts)) return Skip();

        // EVERY pass, before the skip — this is TryAssembleAsync's defense in depth and it must not become
        // a thing that only happens the first time. The gate record carries no binding to the verdicts it
        // was signed over, so an `approved` status alone is not proof the CURRENT analysis was reviewed: a
        // fresh unreviewed non-pass verdict can land under an existing signature. A stage that reached
        // `done` is never lowered again, so there is no second chance to get this right. It writes no step
        // and opens no run — it is a derived status, not work.
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        var stillArmable = RegulatoryGate.Armable(candidates, verdicts).Ok;
        var regStatus = gate?.Status == "approved" && stillArmable ? "done" : StageStatus.AwaitingRe;
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }, ct);

        if (await HasRunAsync(projectId, Stages.Matrix, ct)) return Skip();

        await trail.StepAsync(RunStepKind.Started,
            $"Assembling the compatibility matrix over {verdicts.Count} verdicts.", ct: ct);
        await SetStageAsync(projectId, Stages.Matrix, s => s.Status = "running", ct);

        var componentIds = constraints.Components.Select(k => k.Id).ToList();
        var matrix = MatrixAssembler.Assemble(
            candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O"));
        await store.UpsertMatrixAsync(matrix, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Assembled {matrix.Rows.Count} rows across {componentIds.Count} components.",
            RecordIds.Matrix(projectId));
    }

    private async Task<StageResult> RunDosingAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        // The status, not the DosingDoc: POST /projects/{id}/dosing/loading records a metal loading and
        // re-opens Dosing to `pending`, and that re-open is the ONLY thing that lets an awaiting-operator
        // park ever resume.
        if (await HasRunAsync(projectId, Stages.Dosing, ct)) return Skip();

        // The signature is not self-proving, but its ABSENCE is decisive: Dosing consumes the signed
        // compliant set, so an unsigned gate means there is nothing yet to dose — the pipeline stops here
        // and waits for the R.E. This is the ONE lane where an operator signature is still a precondition
        // rather than a record (design §8 relaxes it; A1 must not).
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        if (gate?.Status != "approved") return Skip();

        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return Skip();

        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        var compliant = CompliantSet.Of(verdicts);

        await trail.StepAsync(RunStepKind.Started,
            $"Dosing {compliant.Count} compliant substances above the measured detection floor.", ct: ct);
        await SetStageAsync(projectId, Stages.Dosing, s => s.Status = "running", ct);

        if (RegulatoryGate.Armable(candidates, verdicts) is { Ok: false } blocked)
            return new StageResult(RunOutcome.NeedsReview,
                "the regulatory gate is signed but no longer covers the current analysis: " +
                string.Join("; ", blocked.Blockers), null, null);

        if (compliant.Count == 0)
            return new StageResult(RunOutcome.NeedsReview,
                "the compliant set is empty — no substance carries an R.E. determination of " +
                "'recommended', so there is nothing that may be dosed.", null, null);

        // Resolve EVERY input first and PARK on any gap — do not run the agent on a partial picture and let
        // it improvise the holes. The two missing things are a MEASUREMENT and a MASS FRACTION; a model
        // that invents either produces a marker nobody can detect or a batch nobody dosed right.
        var (floors, loadings, physicsGaps, loadingGaps) =
            await ResolveDosingInputsAsync(constraints, compliant, ct);
        if (physicsGaps.Count > 0)
            return new StageResult(RunOutcome.NeedsReview, string.Join(" | ", physicsGaps.Distinct()),
                null, null, StageStatus.AwaitingPhysics);
        if (loadingGaps.Count > 0)
            return new StageResult(RunOutcome.NeedsReview,
                "the metal loading (mass fraction of the marker element in the compound) is not on file " +
                "for: " + string.Join(", ", loadingGaps) + ". Enter it once via " +
                "POST /projects/{id}/dosing/loading — it is kept for every future project that uses the " +
                "same compound.", null, null, StageStatus.AwaitingOperator);

        var result = await agents.RunDosingAsync(constraints, compliant, floors, loadings, null, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        var dosing = result.Output!;
        await store.UpsertDosingAsync(dosing, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Finalized {dosing.Codes.Count} codes across " +
            $"{dosing.Codes.Select(k => k.ComponentId).Distinct().Count()} components.",
            RecordIds.Dosing(projectId));
    }

    private async Task<StageResult> RunCostAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await HasRunAsync(projectId, Stages.Cost, ct)) return Skip();
        if (await store.GetDosingAsync(projectId, ct) is not { } d) return Skip();
        if (catalog is null) return Skip(); // degrades safely, as OnDosingAsync did

        // DISTINCT over the finalized codes' markers: one (CAS, element) is audited once even when it
        // appears in several codes or components. The element selects the ref-catalog partition; the CAS
        // is the exact identifier the returned cards are filtered by.
        var substances = d.Codes.SelectMany(k => k.Markers).Select(m => (m.Cas, m.Element)).Distinct().ToList();

        // trail.Run.Agent stays NULL. Cost is a catalog lookup and a price parse; there is nothing here
        // for a model to reason about, and one asked to would only be given the chance to invent a price
        // procurement then acts on. The UI reads the null and says so.
        await trail.StepAsync(RunStepKind.Started,
            $"Pricing {substances.Count} substances against the supplier catalog.", ct: ct);
        await SetStageAsync(projectId, Stages.Cost, s => s.Status = "running", ct);

        var cost = await CostAudit.RunAsync(catalog, substances, projectId,
            DateTimeOffset.UtcNow.ToString("O"), ct);
        await store.UpsertCostAsync(cost, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Priced {substances.Count} substances — " +
            $"{cost.Substances.Count(s => s.BestQuote is not null)} with a parseable quote.",
            RecordIds.Cost(projectId));
    }

    /// The journey's last mile. The decision matrix is DETERMINISTIC assembly over the four upstream
    /// records (DecisionAssembler); only the final-code PICK is an agent, and its output is a PROPOSAL.
    /// The STAGE therefore parks at `awaiting-VP`, never `done`: only the VP gate's signature completes it
    /// — a Decision that went `done` off the agent's own pick would be the agent signing the hard gate.
    private async Task<StageResult> RunDecisionAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await HasRunAsync(projectId, Stages.Decision, ct)) return Skip();
        var dosing = await store.GetDosingAsync(projectId, ct);
        var cost = await store.GetCostAsync(projectId, ct);
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        if (dosing is null || cost is null || constraints is null) return Skip();
        var verdicts = await store.GetVerdictsAsync(projectId, ct);

        // Assemble may throw on a pre-invariant DosingDoc with a duplicate (component, cas) window. It is
        // INSIDE the opened run deliberately: ExecuteAsync's catch stamps `failed` with the message, so
        // the failure is visible rather than a stage silently stuck (§11, nothing dies silently).
        var assembled = DecisionAssembler.Assemble(
            verdicts, dosing, cost, [.. constraints.Components.Select(c => c.Id)]);

        trail.Run.Agent = DecisionAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Picking a final code per component over {assembled.Count} assembled rows.", ct: ct);
        await SetStageAsync(projectId, Stages.Decision, s => s.Status = "running", ct);

        var result = await agents.RunDecisionAsync(assembled, dosing, null, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        var decision = result.Output!;
        decision.Id = RecordIds.Decision(projectId);
        decision.ProjectId = projectId;
        await store.UpsertDecisionAsync(decision, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Proposed a final code for {decision.Components.Count(c => c.ProposedCode is not null)} components.",
            RecordIds.Decision(projectId), StageStatus.AwaitingVp);
    }

    // ---------------------------------------------------------------------------------------------
    // The operator-triggered entry points. These are NOT the pipeline: a gate signature, a revision and
    // a chat turn each arrive from an endpoint, not from RunAsync.
    // ---------------------------------------------------------------------------------------------

    // Trusts the gate record: does NOT re-check arming/completeness here. The false-pass-safety
    // invariant is that POST /regulatory/approve (armable + IsComplete) is the ONLY writer of an
    // approved regulatory GateDoc — and POST /decision/determination (VpGate.Armable + the regulatory
    // coverage re-check + real-code confirmations) the ONLY writer of an approved VP one. Do not add
    // another writer without those checks.
    public async Task OnGateAsync(GateDoc g, CancellationToken ct)
    {
        if (g is { GateType: GateTypes.Regulatory, Status: "approved" })
        {
            await SetStageAsync(g.ProjectId, Stages.Regulatory,
                s => { if (s.Status == StageStatus.AwaitingRe) s.Status = "done"; }, ct);
            // It does NOT re-enter the pipeline. Recording a signature and running agents are separate
            // acts, and conflating them here would mean an endpoint that stamps a gate also spends
            // minutes in Foundry on the caller's thread. The signature UNBLOCKS Dosing (which skips
            // behind an unsigned gate); making that progress happen is the supervisor's job (Task 8).
        }
        else if (g is { GateType: GateTypes.Vp, Status: "approved" })
            await CloseProjectAsync(g.ProjectId, ct);
    }

    /// The VP signature closes the project (spec §4: the VP gate "releases procurement + writes to Marker
    /// Library + Learned Conclusions").
    public async Task CloseProjectAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        // The latch: only the awaiting-VP → done transition closes. Once `done`, a re-signature no-ops here
        // entirely — the knowledge writes are idempotent by deterministic id regardless, but the latch is
        // what keeps them from re-RUNNING at all (re-stamping CreatedAt, re-embedding and re-pushing the
        // conclusion).
        if (project is null || project.Stages[Stages.Decision].Status is not StageStatus.AwaitingVp) return;

        // The whole post-latch body in ONE try: this is the single highest-stakes transition, the only
        // multi-step path talking to remote surfaces beyond the record store (marker-library writes, the
        // conclusion's embed + search push), plus two contract `First()`s. Stamp `failed` rather than let
        // the project sit `awaiting-VP` forever under a signed gate, the dashboard blaming a VP who already
        // signed (§11: nothing dies silently). The zero-confirmation park below is a deliberate RETURN,
        // never an exception — it keeps its own needs-review stamp. Every write inside is idempotent
        // (content-keyed ids, the LinkedProjects pin, the deterministic conclusion id), so once the
        // operator clears the failure a re-signed determination converges.
        try
        {
            // Trust the RECORD, not a caller's snapshot: an approval revoked a moment later would still be
            // handed to us as `approved` while the store already holds `locked`. Closing off that would
            // release procurement under a gate that is no longer signed.
            var gate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            if (gate?.Status != "approved") return;

            var decision = await store.GetDecisionAsync(projectId, ct);
            var dosing = await store.GetDosingAsync(projectId, ct);
            var constraints = await store.GetConstraintsAsync(projectId, ct);
            if (decision is null || dosing is null || constraints is null) return; // nothing signed over nothing

            // The raced-close refusal. The determination endpoint stamps EVERY component before the gate is
            // written, so an unconfirmed component here means the DecisionDoc on file is NOT the one the VP
            // signed — a revision's persist replaced it in the window between the stamp and this call.
            // Filtering to an empty confirmed list and carrying on would release procurement over NOTHING.
            var unconfirmed = decision.Components
                .Where(c => c.ConfirmedCode is null).Select(c => c.ComponentId).ToList();
            if (unconfirmed.Count > 0)
            {
                await SetStageAsync(projectId, Stages.Decision, s =>
                {
                    s.Status = "needs-review";
                    s.Error = "the gate is signed but the decision on file carries no confirmation for: " +
                              string.Join(", ", unconfirmed) +
                              " — a revision may have raced the signature; re-sign after the re-pick";
                }, ct);
                return;
            }

            // The confirmed codes, resolved back to the DosingDoc records they name. The endpoint 422'd any
            // confirmation that names no real code, so First() is a contract here, not a hope.
            var confirmed = decision.Components
                .Where(c => c.ConfirmedCode is not null)
                .Select(c => (Component: c, Code: dosing.Codes.First(
                    k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode)))
                .ToList();

            // Knowledge-null degrade, mirroring the catalog-null Cost path: the writes are skipped, the
            // project still closes.
            if (knowledge is not null)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                foreach (var (component, code) in confirmed)
                {
                    var spec = constraints.Components.First(c => c.Id == component.ComponentId);
                    var id = KnowledgeIds.Marker(MarkerContentKey(code));
                    var existing = await knowledge.GetMarkerAsync(id, ct);
                    if (existing is null)
                        await knowledge.UpsertMarkerAsync(new MarkerLibraryDoc
                        {
                            Id = id,
                            // Ppm is the ANCHOR — the largest marker's ppm, the ratio's "1.00". Together
                            // with the scale-invariant ratio it reconstructs every marker's ppm; storing
                            // any other single number would not.
                            Composition = new([.. code.Markers.Select(m => m.Cas)],
                                code.Markers.Max(m => m.Ppm), code.RatioSignature),
                            ValidatedFor = new(spec.Application, spec.Material, spec.Objective),
                            SourceProject = projectId,
                            LinkedProjects = [projectId],
                            CreatedAt = now,
                        }, ct);
                    else if (!existing.LinkedProjects.Contains(projectId))
                    {
                        // A reuse: another project confirmed the same code. Counted ONCE per project — the
                        // projects-list is the pin, so a re-signature cannot double-count. SourceProject is
                        // provenance and never rewritten.
                        existing.ReuseCount++;
                        existing.LinkedProjects.Add(projectId);
                        await knowledge.UpsertMarkerAsync(existing, ct);
                    }
                }

                var ratios = string.Join("; ",
                    confirmed.Select(c => $"{c.Component.ComponentId}: {c.Component.ConfirmedCode}"));
                await conclusions.WriteAsync(new LearnedConclusionDoc
                {
                    // Deterministic in the project's close — a re-signature upserts one doc.
                    Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Decision, $"{projectId}|close"),
                    Kind = KnowledgeKinds.Decision,
                    Scope = new(null, null, null, null, null, null), // project-wide: the codes span components
                    Finding = $"Project closed under VP approval; confirmed final codes — {ratios}.",
                    // 1.0: this records an operator-signed determination, not an inference.
                    Confidence = 1.0,
                    Provenance = new([projectId],
                        [$"VP determination on project {projectId} — confirmed codes: {ratios}"]),
                    CreatedAt = now,
                }, ct);
            }

            decision.Procurement.Status = ProcurementStatus.Released;
            await store.UpsertDecisionAsync(decision, ct);
            // LAST, deliberately: the stage flip is the latch above, so a crash before this line leaves a
            // re-run whose writes all converge (deterministic ids, the projects-list pin).
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "done"; s.Error = null; }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    /// A library code's identity is its CONTENT — the ratio signature plus every (cas, ppm) pair — so the
    /// same code confirmed by two projects maps to ONE doc (that is what makes reuse countable). Pairs are
    /// ordered by CAS because input order is not identity, and every field is LENGTH-PREFIXED rather than
    /// delimiter-joined (a delimiter that can occur inside a field lets two different codes encode to the
    /// same bytes). SHA-256, never string.GetHashCode — .NET randomises string hashes per process, and this
    /// id must be the same one across every restart forever.
    private static string MarkerContentKey(MarkerCode code)
    {
        var tuple = new System.Text.StringBuilder();
        void Append(string field) => tuple.Append(field.Length).Append(':').Append(field);
        Append(code.RatioSignature);
        foreach (var m in code.Markers.OrderBy(m => m.Cas, StringComparer.Ordinal))
        {
            Append(m.Cas);
            Append(m.Ppm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tuple.ToString()))).ToLowerInvariant()[..16];
    }

    /// The single place both the first run and the revision resolve Dosing's inputs, so the two paths
    /// cannot drift. It computes every floor from the physicist's measured background/device, and every
    /// loading from the cross-project knowledge layer, and returns the GAPS rather than throwing on the
    /// first one — the operator should make ONE trip to the physicist and ONE loading entry, not discover
    /// the holes one park at a time. Each (component, element) and each CAS is attempted exactly once.
    private async Task<(Dictionary<(string, string), Floor> Floors,
                        Dictionary<string, double> Loadings,
                        List<string> PhysicsGaps,
                        List<string> LoadingGaps)>
        ResolveDosingInputsAsync(ConstraintsDoc c, IReadOnlyList<VerdictDoc> compliant, CancellationToken ct)
    {
        var floors = new Dictionary<(string, string), Floor>();
        var loadings = new Dictionary<string, double>();
        var physicsGaps = new List<string>();
        var loadingGaps = new List<string>();
        var floorAttempted = new HashSet<(string, string)>();
        var casAttempted = new HashSet<string>();

        foreach (var v in compliant)
        {
            // The floor key is (component, element): the detection floor is a property of the element's
            // signal against a component's measured background, shared by every compound of that element.
            if (floorAttempted.Add((v.ComponentId, v.Element)))
            {
                var (floor, error) = DetectionFloor.Compute(c.MeasuredBackgrounds, c.Device, v.ComponentId, v.Element);
                if (floor is null) physicsGaps.Add(error!);
                else floors[(v.ComponentId, v.Element)] = floor;
            }

            // The loading key is the CAS: it is a property of the COMPOUND, not the component, so it is
            // looked up (and entered) once per substance. A null store or a null property is a gap, never a
            // guess — an absent loading is not 1.0 (that under-orders an oxide).
            if (casAttempted.Add(v.Cas))
            {
                var property = knowledge is null ? null : await knowledge.GetSubstancePropertyAsync(v.Cas, ct);
                if (property is null) loadingGaps.Add(v.Cas);
                else loadings[v.Cas] = property.MetalLoading;
            }
        }

        return (floors, loadings, physicsGaps, loadingGaps);
    }

    /// Revise-with-reason (Law 4). Re-runs the stage's agent with the operator's directive, voids the gate
    /// their signature no longer covers, and records what was learned.
    public async Task OnRevisionAsync(RevisionDoc r, CancellationToken ct)
    {
        // Only a pending revision is applied — the endpoint that queued it is the only writer of one, and
        // marking it `applied` at the end must not re-enter.
        if (r.Status != RevisionStatus.Pending) return;
        if (await store.GetConstraintsAsync(r.ProjectId, ct) is not { } constraints)
        {
            await FailAsync(r, "project has no constraints — there is no agent output to revise", ct);
            return;
        }

        try
        {
            // ORDER IS THE WHOLE POINT OF THIS METHOD. Every FALLIBLE step runs before anything is MUTATED.
            //
            // 0. The closed-project refusal, hoisted OVER the switch: ONE guard, all four arms. An approved
            //    VP gate is the close, and everything behind it is history — the signed DecisionDoc's
            //    TraceRefs cite the upstream records BY ID, so ANY arm's re-run would replace a cited record
            //    in place; a Discovery/Regulatory re-run would additionally clear the R.E. determination and
            //    void the approved regulatory gate — a CLOSED project reappearing on the dashboard, blocked
            //    on an R.E. who already ruled.
            await ThrowIfClosedAsync(r.ProjectId, r.Stage == Stages.Decision ? "decision" : "project", ct);

            // 1. Re-run the stage's agent. The new output stays in memory — nothing is persisted yet.
            var revised = r.Stage switch
            {
                Stages.Discovery => await ReviseDiscoveryAsync(constraints, r, ct),
                Stages.Regulatory => await ReviseRegulatoryAsync(constraints, r, ct),
                Stages.Dosing => await ReviseDosingAsync(constraints, r, ct),
                Stages.Decision => await ReviseDecisionAsync(constraints, r, ct),
                _ => throw new InvalidOperationException($"stage '{r.Stage}' is not revisable"),
            };

            // 2. Record what was learned. This is the most failure-prone step in the path (a third
            //    consecutive LLM call, a Cosmos upsert, an embedding call, a control-plane index create and
            //    a search push), which is exactly why it runs while there is still nothing to roll back. If
            //    it throws, we land in the catch below with the analysis UNTOUCHED and the revision honestly
            //    `failed` — the operator simply re-issues it.
            r.ConclusionId = await WriteConclusionAsync(r, constraints, revised.StageOutputJson, ct);

            // 3 → 4. ORDER MATTERS between these two: void the gate BEFORE the new output lands, so no
            //    reader can ever see the new analysis under the old signature.
            await VoidRegulatoryGateAsync(r, ct);
            await revised.PersistAsync(ct);

            // 5. Only now is the revision applied.
            r.Status = RevisionStatus.Applied;
            r.AppliedAt = DateTimeOffset.UtcNow.ToString("O");
            r.Error = null;
            await store.UpsertRevisionAsync(r, ct);
        }
        catch (Exception e)
        {
            // RESIDUAL TRADE-OFF, accepted deliberately. If step 3 or 4 fails AFTER the conclusion was
            // written, we are left with an orphan conclusion describing a change that did not land (the
            // revision is `failed` and carries its ConclusionId, so the orphan is at least findable). That is
            // strictly the better failure: the conclusion records the operator's genuine belief, the audit
            // trail is honest, the gate can only have moved in the SAFE direction (voided), and the
            // conclusion id is deterministic in the revision id — so re-issuing the same revision converges
            // rather than duplicating. The inverse — the one this ordering exists to prevent — is a `failed`
            // revision whose change is nevertheless live and permanent.
            await FailAsync(r, e.Message, ct);
        }
    }

    /// The re-run's result, not yet persisted. Revise-with-reason does every FALLIBLE thing (a third LLM
    /// call, an embedding call, a control-plane index create, a search push) BEFORE it mutates anything:
    /// a `failed` revision whose change is nevertheless live would be an audit trail that lies, and the
    /// operator's reason — the one artifact in this system that exists in no corpus — would be lost with
    /// nothing left to retry it.
    private sealed record RevisedStage(string StageOutputJson, Func<CancellationToken, Task> PersistAsync);

    private async Task<RevisedStage> ReviseDiscoveryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // Need-only project: the pool Discovery tiers against lives in the PoolDoc, not the frozen
        // ConstraintsDoc. Hydrate the in-memory constraints from it (the same map RunDiscoveryAsync uses) so
        // the revision re-runs against the same pool the first run did, rather than an empty one that would
        // fail Validate on the first candidate.
        if (c.ElementPools.Count == 0 && await store.GetPoolAsync(r.ProjectId, ct) is { } pool)
            c.ElementPools = PoolElementPools(pool);

        var result = await agents.RunDiscoveryAsync(
            await LoadProjectAsync(r.ProjectId, ct), c, r, NullRunTrail.Instance, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the discovery agent could not apply the revision: {result.Error}");

        var candidates = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(candidates.Substances, Json.Options),
            async token =>
            {
                await store.UpsertCandidatesAsync(candidates, token); // same id ⇒ replaces
                await InvalidateMatrixAsync(r.ProjectId, token);
            });
    }

    private async Task<RevisedStage> ReviseRegulatoryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var candidates = await store.GetCandidatesAsync(r.ProjectId, ct)
            ?? throw new InvalidOperationException("no candidates — Regulatory has not run for this project");
        var candidate = candidates.Substances.FirstOrDefault(s => s.Cas == r.Cas && s.ComponentId == r.ComponentId)
            ?? throw new InvalidOperationException(
                $"the revision targets {r.Cas}|{r.ComponentId}, which is not a candidate in this project");

        var result = await agents.RunRegulatoryAsync(c, candidate, r, NullRunTrail.Instance, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the regulatory agent could not apply the revision: {result.Error}");

        var verdict = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(verdict, Json.Options),
            // The agent's fresh VerdictDoc carries EvidenceReviewed=false and Determination=null by default,
            // so replacing the old one CLEARS the operator's prior ruling — deliberately. That ruling was
            // made against the verdict this one replaces; RegulatoryGate.Armable will now block the gate
            // until the operator opens this item again.
            async token =>
            {
                await store.UpsertVerdictAsync(verdict, token);
                await InvalidateMatrixAsync(r.ProjectId, token);
            });
    }

    /// The matrix is the artifact the operator reads and the XLSX export ships, and it is a pure fold over
    /// (candidates, verdicts) — both of which a Discovery or Regulatory revision has just replaced. Marking
    /// it `pending` is what makes the next pass re-assemble instead of leaving a compliance artifact that is
    /// wrong and looks perfectly current. `failed` is included: a stale matrix under a failed stamp is still
    /// stale. `done` alone would not be enough on a project whose assembly had errored.
    private Task InvalidateMatrixAsync(string projectId, CancellationToken ct) =>
        SetStageAsync(projectId, Stages.Matrix,
            s => { if (s.Status is "done" or "failed") { s.Status = StageStatus.Pending; s.Error = null; } }, ct);

    private async Task<RevisedStage> ReviseDosingAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // Re-resolve the SAME inputs the first run used — the compliant set, the measured floors, the
        // loadings — through the one shared resolver, so the revision path cannot relax what the first run
        // enforced. Validate fires again inside RunDosingAsync, so a directive that would dose below the
        // floor or reach outside the compliant set FAILS here, loudly, with the operator's reason still
        // recorded as a Learned Conclusion. The operator's directive is authoritative over the AGENT; it
        // does not outrank the regulatory gate.
        //
        // Re-check the signed gate BEFORE re-running, exactly as RunDosingAsync does on the first-run path.
        // A Regulatory revision can void the gate (VoidRegulatoryGateAsync locks it) or introduce an
        // unreviewed non-pass verdict since the signature; re-dosing behind a locked-or-uncovered gate would
        // regenerate dosing (and, per the Cost reset below, re-price) over an analysis the operator never
        // gated. Throw so the revision fails cleanly with the analysis untouched.
        var verdicts = await store.GetVerdictsAsync(c.ProjectId, ct);
        var gate = await store.GetGateAsync(c.ProjectId, GateTypes.Regulatory, ct);
        if (gate?.Status != "approved")
            throw new InvalidOperationException(
                "cannot revise Dosing while the regulatory gate is not approved — Dosing consumes the signed " +
                "compliant set; re-dosing an unsigned analysis would produce an artifact the operator never gated");
        var candidates = await store.GetCandidatesAsync(c.ProjectId, ct);
        if (candidates is not null && RegulatoryGate.Armable(candidates, verdicts) is { Ok: false } blocked)
            throw new InvalidOperationException(
                "the regulatory gate is signed but no longer covers the current analysis: " +
                string.Join("; ", blocked.Blockers));
        var compliant = CompliantSet.Of(verdicts);
        var (floors, loadings, physicsGaps, loadingGaps) = await ResolveDosingInputsAsync(c, compliant, ct);
        if (physicsGaps.Count > 0 || loadingGaps.Count > 0)
            throw new InvalidOperationException(
                "cannot revise Dosing while an input is missing: " +
                string.Join("; ", physicsGaps.Concat(loadingGaps)));

        var result = await agents.RunDosingAsync(c, compliant, floors, loadings, r, NullRunTrail.Instance, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the dosing agent could not apply the revision: {result.Error}");

        var dosing = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(dosing, Json.Options),
            async token =>
            {
                // Re-check the close IMMEDIATELY before mutating: the entry check passed minutes ago, and a
                // determination in flight then may have signed since. Without this, the resets below plus
                // the upsert would regenerate the records a just-signed gate covers.
                await ThrowIfClosedAsync(c.ProjectId, "project", token);

                // A Dosing revision may change the codes' substance set, so a Cost audit computed over the
                // OLD set is now stale — the same "never leave an artifact that is wrong but looks current"
                // rule the Matrix gets. Reset Cost to `pending`, which is what InvalidatedAsync reads: the
                // next pass re-prices over the revised substances instead of skipping on the stale doc. A
                // review note does NOT travel this path, so "a review note does not re-price" is preserved.
                await SetStageAsync(c.ProjectId, Stages.Cost,
                    s => { if (s.Status is "done" or "failed") { s.Status = "pending"; s.Error = null; } }, token);
                // ...and Decision with it: the DecisionDoc's rows and proposal were assembled over the OLD
                // dosing/cost, so a project parked `awaiting-VP` would otherwise keep a STALE proposal at the
                // VP's door. `done` is deliberately EXCLUDED: done means the VP signed and the project closed
                // — history, which the refusal above keeps this path off anyway (defense in depth).
                await SetStageAsync(c.ProjectId, Stages.Decision,
                    s => { if (s.Status is StageStatus.AwaitingVp or "needs-review" or "failed")
                           { s.Status = "pending"; s.Error = null; } }, token);
                await store.UpsertDosingAsync(dosing, token);
            });
    }

    /// Revise-with-reason for the PICK. Mirrors ReviseDosingAsync's shape: re-assemble from the LIVE records
    /// through the same deterministic fold the first run used (DecisionAssembler — the revise path cannot
    /// relax what the first run enforced), re-run the pick WITH the directive, and re-park at `awaiting-VP`.
    /// An unsigned/locked vp gate is left exactly as it stands: locked is already the safe state a void would
    /// produce, and nothing on this path may move a gate toward `approved` (Law 9).
    private async Task<RevisedStage> ReviseDecisionAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // Captured NOW so the persist closure can prove the stage did not move while the agent ran.
        var statusAtStart = (await store.GetProjectAsync(c.ProjectId, ct))
            ?.Stages.GetValueOrDefault(Stages.Decision)?.Status;

        var verdicts = await store.GetVerdictsAsync(c.ProjectId, ct);
        var dosing = await store.GetDosingAsync(c.ProjectId, ct)
            ?? throw new InvalidOperationException("no dosing on file — there are no finalized codes to re-pick over");
        var cost = await store.GetCostAsync(c.ProjectId, ct)
            ?? throw new InvalidOperationException("no cost audit on file — Decision has not run for this project");

        // Assemble may throw (the pre-invariant duplicate-window ArgumentException); here the
        // OnRevisionAsync catch turns that into an honestly-failed revision, analysis untouched.
        var assembled = DecisionAssembler.Assemble(
            verdicts, dosing, cost, [.. c.Components.Select(k => k.Id)]);

        var result = await agents.RunDecisionAsync(assembled, dosing, r, NullRunTrail.Instance, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the decision agent could not apply the revision: {result.Error}");

        var decision = result.Output!;
        decision.Id = RecordIds.Decision(c.ProjectId);
        decision.ProjectId = c.ProjectId;
        return new RevisedStage(
            JsonSerializer.Serialize(decision, Json.Options),
            async token =>
            {
                // Re-check the world IMMEDIATELY before writing. The run between the entry checks and this
                // line is minutes wide (two LLM calls, an embed, a push), and the stage advertised
                // `awaiting-VP` throughout — so a determination STARTED before this revision landed can have
                // completed mid-run. If the VP signed in that window, persisting would put an unconfirmed doc
                // OVER the stamped one — under an approved gate whose close then finds zero confirmations.
                await ThrowIfClosedAsync(c.ProjectId, "decision", token);
                var now = (await store.GetProjectAsync(c.ProjectId, token))
                    ?.Stages.GetValueOrDefault(Stages.Decision)?.Status;
                if (now != statusAtStart)
                    throw new InvalidOperationException(
                        $"the decision stage moved from '{statusAtStart ?? "absent"}' to '{now ?? "absent"}' " +
                        "while the revision was re-running — refusing to persist over a record that changed " +
                        "mid-flight; re-issue the revision");

                // Doc FIRST, park SECOND — the park is the "a proposal awaits your signature" signal, and
                // POST /decision/determination signs whatever DecisionDoc is on file at `awaiting-VP`. The
                // reverse order opens a window where the stage advertises the park while the STALE proposal
                // is still the one on file.
                await store.UpsertDecisionAsync(decision, token);
                await SetStageAsync(c.ProjectId, Stages.Decision,
                    s => { s.Status = StageStatus.AwaitingVp; s.Error = null; }, token);
            });
    }

    /// An approved VP gate IS the project's close, and everything behind it is history — the Marker Library
    /// entry, the close conclusion, the released procurement all cite the SIGNED decision. Any revision that
    /// would regenerate an analytical record on a closed project is refused outright, before the agent runs
    /// and before anything is re-priced; revising history is a new project decision.
    private async Task ThrowIfClosedAsync(string projectId, string what, CancellationToken ct)
    {
        if ((await store.GetGateAsync(projectId, GateTypes.Vp, ct))?.Status == "approved")
            throw new InvalidOperationException(
                $"the project is closed — the VP signature is history; revising a closed {what} requires a new project");
    }

    /// A gate is an operator's signature over a SPECIFIC analysis, and the revision just replaced that
    /// analysis. Leaving the signature standing is the false pass: a stage that already reached `done` is
    /// never lowered again, so an approved-and-done Regulatory stage would silently absorb verdicts the
    /// operator never reviewed. Void it and make them sign again.
    private async Task VoidRegulatoryGateAsync(RevisionDoc r, CancellationToken ct)
    {
        // BreaksRegulatoryGate is a partial function — it throws for a non-revisable stage rather than
        // returning the dangerous `false`. We only reach here for a revisable stage, but ask explicitly.
        if (!RevisionEffects.IsRevisable(r.Stage) || !RevisionEffects.BreaksRegulatoryGate(r.Stage)) return;

        if (await store.GetGateAsync(r.ProjectId, GateTypes.Regulatory, ct) is { Status: "approved" } gate)
        {
            gate.Status = "locked";
            gate.ApprovedAt = null;
            await store.UpsertGateAsync(gate, ct);
        }
        await SetStageAsync(r.ProjectId, Stages.Regulatory,
            s => { if (s.Status == "done") s.Status = StageStatus.AwaitingRe; }, ct);
    }

    private async Task<string> WriteConclusionAsync(
        RevisionDoc r, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct)
    {
        var kind = RevisionEffects.ConclusionKind(r.Stage);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var distilled = await agents.RunConclusionAsync(r, constraints, stageOutputJson, ct);

        var doc = new LearnedConclusionDoc
        {
            Id = KnowledgeIds.RevisionConclusion(kind, r.Id),
            Kind = kind,
            // If the distiller could not produce a valid conclusion we still record the operator's reason
            // VERBATIM rather than dropping it — silently discarding the "why" would break Law 4's promise
            // that every change-with-a-reason teaches the system something.
            Scope = distilled.Succeeded ? distilled.Output!.Scope : new(null, null, null, null, null, null),
            Finding = distilled.Succeeded
                ? distilled.Output!.Finding
                : $"Operator revised {r.Stage} — {r.Target}: {r.Reason}",
            Confidence = distilled.Succeeded ? distilled.Output!.Confidence : 0.5,
            // Provenance is CODE-owned, always. The operator's reason must reach the knowledge layer word
            // for word: a model permitted to paraphrase "overlaps the Ti K-beta line" into "improved
            // tiering" would erase the only part of the record that is worth keeping.
            Provenance = new([r.ProjectId], [$"revision {r.Id} — target: {r.Target} — operator reason: {r.Reason}"]),
            CreatedAt = now,
        };
        await conclusions.WriteAsync(doc, ct);
        return doc.Id;
    }

    private async Task FailAsync(RevisionDoc r, string error, CancellationToken ct)
    {
        r.Status = RevisionStatus.Failed;
        r.Error = error;
        await store.UpsertRevisionAsync(r, ct);
    }

    /// A chat turn (design §5). The reply is a record, so the conversation survives a restart and a
    /// multi-day re-entry (Law 6) — the agent itself remembers nothing.
    public async Task OnChatMessageAsync(ChatMessageDoc fed, CancellationToken ct)
    {
        // RE-READ rather than trust the doc the caller handed us. The idempotency of this whole handler
        // rests on the status being the CURRENT one — a stale `pending` re-runs a turn that may already
        // have queued a revision. A partition-scoped point read costs about one RU.
        //
        // It is also what makes the read-modify-write below sound: `m` is the store's own current doc, so
        // flipping its status writes back what is actually there instead of blind-overwriting with a stale
        // payload. And a message the caller named that the store does not have cannot exist — if it somehow
        // does, upserting it would CONJURE a message nobody sent, so do nothing.
        if (await store.GetChatMessageAsync(fed.ProjectId, fed.Id, ct) is not { } m) return;

        // Only the first delivery acts. A re-sent message must not re-run an agent that may already have
        // queued a revision.
        if (m.Status != ChatStatus.Pending) return;

        try
        {
            // PRIOR conversation only — the message being answered is excluded. It is already in the record
            // by the time this runs, so an unfiltered thread would put it in "CONVERSATION SO FAR" and then
            // repeat it under "THE OPERATOR'S NEW MESSAGE".
            //
            // The duplication is the lesser half. The real damage is on a FIRST turn: the agent would be
            // shown a one-message "conversation so far" instead of ChatThread.Render's "(there is no prior
            // conversation)" — which is an invitation to treat the operator's opening question as context it
            // has already dealt with, and to answer around a history it never had.
            //
            // BY ID, never by "everything before m.CreatedAt": two turns can share a timestamp — that is why
            // ChatTurns.InOrder carries a tiebreak at all — so a time-based predicate is a filter that works
            // until the day two writes land on one tick, and then silently eats a turn.
            var prior = (await store.GetChatThreadAsync(m.ProjectId, m.Stage, ct)).Where(t => t.Id != m.Id);
            var thread = ChatThread.Render([.. prior]);
            var inputs = await StageInputsJsonAsync(m.ProjectId, m.Stage, ct);

            // Bound to THIS project and THIS stage. The model has no parameter with which to name another.
            var chatTools = new ChatTools(store, m.ProjectId, m.Stage, KeyOf(m.Id));
            var text = await agents.RunChatAsync(chatTools, thread, inputs, m.Text, ct);

            // KNOWN GAP, left open deliberately. By the time we reach this line an `apply_revision` call has
            // already written a DURABLE RevisionDoc, and the message does not leave `pending` until the two
            // writes below land. A crash in that window leaves a still-`pending` message and the turn
            // RE-RUNS on a re-send.
            //
            // GUARANTEED: an identical apply_revision call converges. ChatTools content-addresses the
            // revision id from (chat key + call content) and refuses to overwrite one that has already left
            // `pending`, so the same call cannot queue a second revision or file a second Learned Conclusion.
            // NOT GUARANTEED: that the replay makes the same call.
            await store.UpsertChatReplyAsync(new ChatReplyDoc
            {
                // Derived from the message's key, so a re-send upserts one reply instead of appending.
                Id = RecordIds.ChatReply(m.ProjectId, m.Stage, KeyOf(m.Id)),
                ProjectId = m.ProjectId, Stage = m.Stage, MessageId = m.Id,
                Text = text,
                // COPIED, not aliased: `Trail` is a live List the ChatTools instance still owns.
                // ChatToolCall is an immutable record, so a shallow copy is a complete one, and the reply's
                // audit trail is frozen at the instant the turn ended rather than tracking a list someone
                // could still append to.
                ToolCalls = [.. chatTools.Trail],
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            m.Status = ChatStatus.Answered;
            m.Error = null;
            await store.UpsertChatMessageAsync(m, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The SERVICE is stopping — the agent did not fail. `failed` is terminal (nothing re-runs a
            // failed message), so recording a shutdown as one would tell the operator permanently that their
            // question came back as "A task was canceled". Leave it `pending`: that is the truth — it was
            // never answered — and it is the only status a re-send can act on. Rethrow so the host logs a
            // stop rather than swallowing it as an answered turn.
            //
            // The `when` filter is load-bearing: an OperationCanceledException NOT tied to our token is a
            // real failure (an HTTP timeout inside the model call surfaces as exactly this type).
            throw;
        }
        catch (Exception e)
        {
            // No half-written reply: the operator must never read a partial answer as the agent's word. The
            // prefix is there for the same reason — this text is rendered in the conversation, and a bare
            // "429" or "The SSL connection could not be established" reads as something the AGENT said.
            m.Status = ChatStatus.Failed;
            m.Error = $"the agent could not complete this turn: {e.Message}";
            await store.UpsertChatMessageAsync(m, ct);
        }
    }

    /// The stage's current record inputs — what the agent is answering ABOUT. Without them the turn is an
    /// agent reasoning from a transcript alone, about an analysis it cannot see.
    private async Task<string> StageInputsJsonAsync(string projectId, string stage, CancellationToken ct) => stage switch
    {
        Stages.Intake => JsonSerializer.Serialize(await store.GetProjectAsync(projectId, ct), Json.Options),
        Stages.Discovery => JsonSerializer.Serialize(await store.GetCandidatesAsync(projectId, ct), Json.Options),
        Stages.Regulatory => JsonSerializer.Serialize(await store.GetVerdictsAsync(projectId, ct), Json.Options),
        Stages.Matrix => JsonSerializer.Serialize(await store.GetMatrixAsync(projectId, ct), Json.Options),
        Stages.Dosing => JsonSerializer.Serialize(await store.GetDosingAsync(projectId, ct), Json.Options),
        Stages.Cost => JsonSerializer.Serialize(await store.GetCostAsync(projectId, ct), Json.Options),
        Stages.Decision => JsonSerializer.Serialize(await store.GetDecisionAsync(projectId, ct), Json.Options),
        _ => "{}",
    };

    /// The message's KEY — the suffix RecordIds.ChatMessage was minted with, not the whole id. ChatTools
    /// concatenates it into further Cosmos ids and asserts it is an id-safe token; the full id contains '|'.
    private static string KeyOf(string chatMessageId) => chatMessageId.Split('|')[^1];

    /// Discovery is the one stage that can reach the public internet, and the ProjectDoc carries the terms
    /// (client, product, project id) its web-search tool must refuse to send. THROWS when the project is
    /// missing rather than substituting an empty stand-in: no project ⇒ no sensitive terms ⇒ a Discovery run
    /// with an unguarded external search. A stage that lands in `failed` is recoverable; a leaked client name
    /// is not.
    private async Task<ProjectDoc> LoadProjectAsync(string projectId, CancellationToken ct) =>
        await store.GetProjectAsync(projectId, ct)
        ?? throw new InvalidOperationException($"project {projectId} not found");

    private async Task SetStageAsync(string projectId, string stage, Action<StageState> mutate, CancellationToken ct)
    {
        if (await store.GetProjectAsync(projectId, ct) is not { } project) return;
        mutate(project.Stages[stage]);
        await store.UpsertProjectAsync(project, ct);
    }
}
