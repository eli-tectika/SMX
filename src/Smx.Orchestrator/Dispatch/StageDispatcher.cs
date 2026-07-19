using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Cost;
using Smx.Orchestrator.Knowledge;

namespace Smx.Orchestrator.Dispatch;

/// Reacts to record changes. Change feed is at-least-once: every branch must be idempotent
/// (re-check store state before acting) and every write an upsert.
///
/// <paramref name="knowledge"/> is an OPTIONAL trailing parameter deliberately: it is read only by the Dosing
/// path (metal loadings live in the cross-project knowledge layer, not on the per-project bus), and making it
/// required would force every construction site — including the tests that predate Dosing — to change. When it
/// is null, Dosing treats every loading as unknown and parks in `awaiting-operator` rather than guessing: it
/// degrades safely. DEFERRED: the production DI (Orchestrator/Program.cs) must pass the real IKnowledgeStore
/// so Dosing can actually resolve loadings — see the note on TryDoseAsync.
///
/// <paramref name="catalog"/> is OPTIONAL for the SAME reason: it is read only by the Cost path, and a required
/// parameter would force every construction site — including the forbidden Program.cs and the tests that
/// predate Cost — to change. When it is null, Cost degrades safely: OnDosingAsync returns without pricing and
/// the stage stays `pending`, never fabricating an audit from an absent catalog. DEFERRED: the production DI
/// (Orchestrator/Program.cs) must pass the real ICatalogLookup — see the note on OnDosingAsync.
public sealed class StageDispatcher(
    IRecordStore store, IAgentRuns agents, ILearnedConclusionWriter conclusions, int regulatoryParallelism,
    IKnowledgeStore? knowledge = null, ICatalogLookup? catalog = null)
{
    public async Task OnRecordChangedAsync(object doc, CancellationToken ct)
    {
        switch (doc)
        {
            case ProjectDoc p: await OnProjectAsync(p, ct); break;
            case ConstraintsDoc c: await OnConstraintsAsync(c, ct); break;
            case CandidatesDoc cd: await OnCandidatesAsync(cd, ct); break;
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case GateDoc g: await OnGateAsync(g, ct); break;
            case RevisionDoc r: await OnRevisionAsync(r, ct); break;
            case ChatMessageDoc m: await OnChatMessageAsync(m, ct); break;
            case DosingDoc d: await OnDosingAsync(d, ct); break;
            case CostDoc c: await TryDecideAsync(c.ProjectId, ct); break;
            case MatrixDoc: break; // terminal
            // Terminal too, and spelled out rather than left to fall out of the switch. The DecisionDoc is
            // the Decision stage's OUTPUT: the runner that wrote it already parked the stage at
            // `awaiting-VP` in the same breath, so its delivery has nothing left to do. The CLOSE hangs off
            // the VP GateDoc (OnGateAsync → CloseProjectAsync) — a SIGNATURE, never the analysis it covers.
            // Dispatching anything here would act on the decision ahead of the signature authorizing it.
            case DecisionDoc: break;
        }
    }

    private async Task OnProjectAsync(ProjectDoc p, CancellationToken ct)
    {
        await MaybeRunIntakeAsync(p, ct);

        // The Dosing RE-ENTRY. POST /dosing/loading (Task 13) records a metal loading and re-opens Dosing to
        // `pending` — which upserts the ProjectDoc, and THAT is the change the feed delivers here. Without this
        // call the loading the operator just entered would reach nothing: the awaiting-operator park would be
        // permanent. TryDoseAsync is idempotent (it acts only when Dosing is `pending` and the gate is signed),
        // so calling it on every project upsert is safe.
        await TryDoseAsync(p.ProjectId, ct);
    }

    private async Task MaybeRunIntakeAsync(ProjectDoc p, CancellationToken ct)
    {
        if (p.Stages[Stages.Intake].Status != "pending") return;
        if (await store.GetConstraintsAsync(p.ProjectId, ct) is not null) return;
        await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunIntakeAsync(p, ct);
            if (result.Succeeded)
            {
                await store.UpsertConstraintsAsync(result.Output!, ct);
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnConstraintsAsync(ConstraintsDoc c, CancellationToken ct)
    {
        if (await store.GetCandidatesAsync(c.ProjectId, ct) is not null) return; // idempotency
        await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            // Known-candidate mode: bypass the Discovery agent when the operator/eval supplied candidates.
            if (c.ProvidedCandidates.Count > 0)
            {
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
                    await SetStageAsync(c.ProjectId, Stages.Discovery, s =>
                    {
                        s.Status = "needs-review";
                        s.Error = $"provided candidate {named} — which fails its CAS check digit. " +
                                  "Correct the CAS against a primary source and re-submit; it is not safe to " +
                                  "screen, dose or order a substance identified by a CAS that is provably wrong.";
                    }, ct);
                    return;
                }

                await store.UpsertCandidatesAsync(new CandidatesDoc
                {
                    Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
                    Substances = [.. c.ProvidedCandidates],
                }, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
                return;
            }
            var result = await agents.RunDiscoveryAsync(await LoadProjectAsync(c.ProjectId, ct), c, null, ct);
            if (result.Succeeded)
            {
                await store.UpsertCandidatesAsync(result.Output!, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnCandidatesAsync(CandidatesDoc cd, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(cd.ProjectId, ct);
        if (constraints is null) return;
        var existing = (await store.GetVerdictsAsync(cd.ProjectId, ct)).Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = cd.Substances.Where(s => s.Tier != "C" && !existing.Contains((s.Cas, s.ComponentId))).ToList();
        if (missing.Count == 0) { await TryAssembleAsync(cd.ProjectId, ct); return; }

        await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "running"; s.Attempts++; }, ct);
        using var gate = new SemaphoreSlim(regulatoryParallelism);
        var tasks = missing.Select(async candidate =>
        {
            await gate.WaitAsync(ct);
            try
            {
                try
                {
                    var result = await agents.RunRegulatoryAsync(constraints, candidate, null, ct);
                    var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                    {
                        Id = RecordIds.Verdict(cd.ProjectId, candidate.Cas, candidate.ComponentId),
                        ProjectId = cd.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
                        Element = candidate.Element, Form = candidate.Form,
                        Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [],
                            0, $"agent could not produce a valid cited verdict: {result.Error}")],
                    };
                    await store.UpsertVerdictAsync(verdict, ct);
                }
                catch (Exception e)
                {
                    await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        await TryAssembleAsync(cd.ProjectId, ct);
    }

    private Task OnVerdictAsync(VerdictDoc v, CancellationToken ct) => TryAssembleAsync(v.ProjectId, ct);

    // Trusts the gate record: does NOT re-check arming/completeness here. The false-pass-safety
    // invariant is that POST /regulatory/approve (armable + IsComplete) is the ONLY writer of an
    // approved regulatory GateDoc — and POST /decision/determination (VpGate.Armable + the regulatory
    // coverage re-check + real-code confirmations) the ONLY writer of an approved VP one. Do not add
    // another writer without those checks.
    private async Task OnGateAsync(GateDoc g, CancellationToken ct)
    {
        if (g is { GateType: GateTypes.Regulatory, Status: "approved" })
        {
            await SetStageAsync(g.ProjectId, Stages.Regulatory,
                s => { if (s.Status == "awaiting-RE") s.Status = "done"; }, ct);

            // The signature IS the dispatch (record-as-bus). The operator's approval of the regulatory gate is
            // the record that says "the compliant set is final", so it — not the MatrixDoc — is what triggers
            // Dosing. The MatrixDoc is NOT a trigger (`case MatrixDoc: break;`) precisely because
            // TryAssembleAsync upserts it on verdict COMPLETENESS, before any signature: dosing off the matrix
            // would dose an UNSIGNED gate — the hard regulatory gate bypassed by the stage right after it.
            await TryDoseAsync(g.ProjectId, ct);
        }
        else if (g is { GateType: GateTypes.Vp, Status: "approved" })
            await CloseProjectAsync(g.ProjectId, ct);
    }

    /// The VP signature closes the project (spec §4: the VP gate "releases procurement + writes to Marker
    /// Library + Learned Conclusions"). The approved VP GateDoc landing on the change feed IS the close
    /// dispatch — writing the record is the trigger, same as every other stage.
    private async Task CloseProjectAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        // The idempotency latch: only the awaiting-VP → done transition closes. Once `done`, redeliveries
        // no-op here entirely — the knowledge writes are idempotent by deterministic id regardless, but the
        // latch is what keeps them from re-RUNNING at all (re-stamping CreatedAt, re-embedding and
        // re-pushing the conclusion on every at-least-once delivery).
        if (project is null || project.Stages[Stages.Decision].Status is not "awaiting-VP") return;

        // The whole post-latch body in ONE try (every stage runner's posture): this is the single
        // highest-stakes transition, the only multi-step dispatch path talking to remote surfaces beyond
        // the record store (marker-library writes, the conclusion's embed + search push), plus two
        // contract `First()`s. An exception that escaped here would hit ChangeFeedWorker — which logs and
        // CHECKPOINTS ANYWAY — so the failure mode is checkpoint-and-lose: the project sits `awaiting-VP`
        // forever under a signed gate, the dashboard blaming a VP who already signed. Stamp `failed`
        // instead (§11: nothing dies silently). The zero-confirmation park below is a deliberate RETURN,
        // never an exception — it keeps its own needs-review stamp. Every write inside is idempotent
        // (content-keyed ids, the LinkedProjects pin, the deterministic conclusion id), so once the
        // operator clears the failure a re-signed determination converges.
        try
        {
            // F3: trust the RECORD, not the fed snapshot (the OnChatMessageAsync lesson). The feed's element
            // is a snapshot of the gate at some change: an approval revoked a moment later delivers the
            // approved version while the store already holds `locked`. Closing off the fed element would
            // release procurement under a gate that is no longer signed — re-read, and a by-now-unsigned
            // gate closes nothing (the reject's own delivery is a no-op in OnGateAsync).
            var gate = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
            if (gate?.Status != "approved") return;

            var decision = await store.GetDecisionAsync(projectId, ct);
            var dosing = await store.GetDosingAsync(projectId, ct);
            var constraints = await store.GetConstraintsAsync(projectId, ct);
            if (decision is null || dosing is null || constraints is null) return; // nothing signed over nothing

            // The raced-close refusal (Task 15 review F1, layer 1). The determination endpoint stamps EVERY
            // component before the gate is written, so an unconfirmed component here means the DecisionDoc on
            // file is NOT the one the VP signed — a revision's persist replaced it in the window between the
            // stamp and this delivery. Filtering to an empty confirmed list and carrying on would release
            // procurement over NOTHING (an empty conclusion under a real signature). Park LOUD instead;
            // the re-pick re-parks at awaiting-VP and the VP signs what they can actually read.
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

            // Knowledge-null degrade, mirroring the catalog-null Cost path: the writes are skipped, the project
            // still closes. DEFERRED like the others: production DI must pass the real IKnowledgeStore.
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
                            // Ppm is the ANCHOR — the largest marker's ppm, the ratio's "1.00". Together with
                            // the scale-invariant ratio it reconstructs every marker's ppm; storing any other
                            // single number would not.
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
                        // projects-list is the pin, so a redelivered gate cannot double-count (see
                        // MarkerLibraryDoc.LinkedProjects). SourceProject is provenance and never rewritten.
                        existing.ReuseCount++;
                        existing.LinkedProjects.Add(projectId);
                        await knowledge.UpsertMarkerAsync(existing, ct);
                    }
                }

                var ratios = string.Join("; ", confirmed.Select(c => $"{c.Component.ComponentId}: {c.Component.ConfirmedCode}"));
                await conclusions.WriteAsync(new LearnedConclusionDoc
                {
                    // Deterministic in the project's close — an at-least-once redelivery upserts one doc.
                    Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Decision, $"{projectId}|close"),
                    Kind = KnowledgeKinds.Decision,
                    Scope = new(null, null, null, null, null, null), // project-wide: the codes span components
                    Finding = $"Project closed under VP approval; confirmed final codes — {ratios}.",
                    // 1.0: this records an operator-signed determination, not an inference.
                    Confidence = 1.0,
                    Provenance = new([projectId], [$"VP determination on project {projectId} — confirmed codes: {ratios}"]),
                    CreatedAt = now,
                }, ct);
            }

            decision.Procurement.Status = ProcurementStatus.Released;
            await store.UpsertDecisionAsync(decision, ct);
            // LAST, deliberately: the stage flip is the latch above, so a crash before this line redelivers
            // into a re-run whose writes all converge (deterministic ids, the projects-list pin).
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "done"; s.Error = null; }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    /// A library code's identity is its CONTENT — the ratio signature plus every (cas, ppm) pair — so the
    /// same code confirmed by two projects maps to ONE doc (that is what makes reuse countable), and a
    /// redelivered gate upserts rather than appends. Pairs are ordered by CAS because input order is not
    /// identity, and every field is LENGTH-PREFIXED rather than delimiter-joined (the Plan-3c /
    /// ChatTools.ContentKey lesson: a delimiter that can occur inside a field lets two different codes
    /// encode to the same bytes). SHA-256, never string.GetHashCode — .NET randomises string hashes per
    /// process, and this id must be the same one across every restart forever.
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

    /// The shared Dosing resolver, called from TWO places — OnGateAsync (the signature triggers the stage) and
    /// OnProjectAsync (the re-entry, after POST /dosing/loading re-opens Dosing to `pending`). It resolves
    /// EVERY input first and PARKS on any gap rather than running the agent on a partial picture: the two
    /// missing things are a MEASUREMENT and a MASS FRACTION, and a model that invents either produces a marker
    /// nobody can detect or a batch nobody dosed right.
    private async Task TryDoseAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        // At-least-once feed + the re-entry point (POST /dosing/loading sets Dosing back to `pending`).
        if (project is null || project.Stages[Stages.Dosing].Status is not "pending") return;

        // The signature is not self-proving. Re-check it, for the reason TryAssembleAsync gives: the gate record
        // carries no binding to the verdicts it was signed over, so `approved` alone is not proof the CURRENT
        // analysis was reviewed.
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        if (gate?.Status != "approved") return;

        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return;

        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (RegulatoryGate.Armable(candidates, verdicts) is { Ok: false } blocked)
        {
            await SetStageAsync(projectId, Stages.Dosing, s =>
            {
                s.Status = "needs-review";
                s.Error = "the regulatory gate is signed but no longer covers the current analysis: " +
                          string.Join("; ", blocked.Blockers);
            }, ct);
            return;
        }

        var compliant = CompliantSet.Of(verdicts);
        if (compliant.Count == 0)
        {
            await SetStageAsync(projectId, Stages.Dosing, s =>
            {
                s.Status = "needs-review";
                s.Error = "the compliant set is empty — no substance carries an R.E. determination of " +
                          "'recommended', so there is nothing that may be dosed.";
            }, ct);
            return;
        }

        // Resolve EVERY input first and PARK on any gap — do not run the agent on a partial picture and let it
        // improvise the holes. The two missing things are a MEASUREMENT and a MASS FRACTION; a model that invents
        // either produces a marker nobody can detect or a batch nobody dosed right.
        var (floors, loadings, physicsGaps, loadingGaps) = await ResolveDosingInputsAsync(constraints, compliant, ct);
        if (physicsGaps.Count > 0)
        {
            await SetStageAsync(projectId, Stages.Dosing,
                s => { s.Status = "awaiting-physics"; s.Error = string.Join(" | ", physicsGaps.Distinct()); }, ct);
            return;
        }
        if (loadingGaps.Count > 0)
        {
            await SetStageAsync(projectId, Stages.Dosing, s =>
            {
                s.Status = "awaiting-operator";
                s.Error = "the metal loading (mass fraction of the marker element in the compound) is not on file " +
                          "for: " + string.Join(", ", loadingGaps) + ". Enter it once via " +
                          "POST /projects/{id}/dosing/loading — it is kept for every future project that uses the " +
                          "same compound.";
            }, ct);
            return;
        }

        await SetStageAsync(projectId, Stages.Dosing, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunDosingAsync(constraints, compliant, floors, loadings, null, ct);
            if (!result.Succeeded)
            {
                await SetStageAsync(projectId, Stages.Dosing,
                    s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
                return;
            }
            await store.UpsertDosingAsync(result.Output!, ct);
            await SetStageAsync(projectId, Stages.Dosing, s => { s.Status = "done"; s.Error = null; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(projectId, Stages.Dosing, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    /// Dosing finished — the finalized codes name what will actually be ordered, so Cost audits exactly
    /// those substances. Deterministic: no agent (§3.4). Cost is a supplier-catalog lookup and a price parse;
    /// there is nothing here for a model to reason about, and a model asked to would only be given the chance
    /// to invent a price procurement then acts on.
    private async Task OnDosingAsync(DosingDoc d, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(d.ProjectId, ct);
        // Guard on the STAGE STATUS, not on "does a DosingDoc exist". The soft code-finalization checkpoint
        // (POST /dosing/review) upserts the SAME DosingDoc to record a review note — and that upsert
        // re-delivers here. If this guarded on the doc's existence it would re-price the whole project every
        // time the operator recorded a note. Only `pending` runs — which the first Cost run flips to `done`,
        // absorbing every later redelivery (the at-least-once feed, and the review-note upsert alike).
        if (project is null || project.Stages[Stages.Cost].Status is not "pending") return;
        if (catalog is null) return;   // production DI wiring pending; degrades safely (Cost stays pending)

        // DISTINCT over the finalized codes' markers: one (CAS, element) is audited once even when it appears
        // in several codes or components. The element selects the ref-catalog partition; the CAS is the exact
        // identifier the returned cards are filtered by.
        var substances = d.Codes.SelectMany(c => c.Markers).Select(m => (m.Cas, m.Element)).Distinct().ToList();
        await SetStageAsync(d.ProjectId, Stages.Cost, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var cost = await CostAudit.RunAsync(catalog, substances, d.ProjectId, DateTimeOffset.UtcNow.ToString("O"), ct);
            await store.UpsertCostAsync(cost, ct);
            await SetStageAsync(d.ProjectId, Stages.Cost, s => { s.Status = "done"; s.Error = null; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(d.ProjectId, Stages.Cost, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    /// Cost finished — the journey's last mile. The decision matrix is DETERMINISTIC assembly over the four
    /// upstream records (DecisionAssembler); only the final-code PICK is an agent, and its output is a
    /// PROPOSAL. The stage therefore parks at `awaiting-VP`, never `done`: only the VP gate's signature
    /// (Task 9) completes it — a Decision that went `done` off the agent's own pick would be the agent
    /// signing the hard gate (Law 9).
    private async Task TryDecideAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        // Guard on the STAGE STATUS, not on "does a DecisionDoc exist" — the OnDosingAsync lesson. The two
        // diverge exactly when it matters: a failed/needs-review run persists NO doc, and a doc-existence
        // guard would re-run the pick on every redelivery of the same CostDoc.
        if (project is null || project.Stages[Stages.Decision].Status is not "pending") return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        var dosing = await store.GetDosingAsync(projectId, ct);
        var cost = await store.GetCostAsync(projectId, ct);
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        if (dosing is null || cost is null || constraints is null) return; // inputs first; the feed will redeliver

        await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            // INSIDE the try, deliberately (the Tasks-3-5 review amendment): a pre-invariant persisted
            // DosingDoc with a duplicate (component, cas) window makes Assemble's ToDictionary throw
            // ArgumentException. Outside the try that throw escapes into the ChangeFeedWorker — which
            // CATCHES dispatch exceptions, logs, and CHECKPOINTS the batch anyway. So the failure mode is
            // checkpoint-and-lose: the stage sits silently stuck at `pending` (nothing left on the feed to
            // redeliver it), not an endless redelivery loop. The amendment's value is the visible `failed`
            // stamp with the error surfaced — §11's "nothing dies silently" — not loop prevention.
            var assembled = DecisionAssembler.Assemble(
                verdicts, dosing, cost, [.. constraints.Components.Select(c => c.Id)]);

            var result = await agents.RunDecisionAsync(assembled, dosing, null, ct);
            if (!result.Succeeded)
            {
                await SetStageAsync(projectId, Stages.Decision,
                    s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
                return;
            }
            result.Output!.Id = RecordIds.Decision(projectId);
            result.Output.ProjectId = projectId;
            await store.UpsertDecisionAsync(result.Output, ct);
            // awaiting-VP, NOT done: the stage completes only when the VP gate is signed (Task 9 flips it).
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "awaiting-VP"; s.Error = null; }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    /// The single place both the first run (this task) and the revision (Task 14) resolve Dosing's inputs, so
    /// the two paths cannot drift. It computes every floor from the physicist's measured background/device, and
    /// every loading from the cross-project knowledge layer, and returns the GAPS rather than throwing on the
    /// first one — the operator should make ONE trip to the physicist and ONE loading entry, not discover the
    /// holes one park at a time. Each (component, element) and each CAS is attempted exactly once.
    ///
    /// DEFERRED PRODUCTION WIRING: when `knowledge` is null every loading is reported as a gap, so Dosing parks
    /// safely in awaiting-operator. That is the degraded mode until Orchestrator/Program.cs passes the real
    /// IKnowledgeStore into StageDispatcher's optional trailing parameter (the store is already registered as a
    /// singleton there — only the constructor call needs the extra argument).
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
            // The floor key is (component, element): the detection floor is a property of the element's signal
            // against a component's measured background, shared by every compound of that element in it.
            if (floorAttempted.Add((v.ComponentId, v.Element)))
            {
                var (floor, error) = DetectionFloor.Compute(c.MeasuredBackgrounds, c.Device, v.ComponentId, v.Element);
                if (floor is null) physicsGaps.Add(error!);
                else floors[(v.ComponentId, v.Element)] = floor;
            }

            // The loading key is the CAS: it is a property of the COMPOUND, not the component, so it is looked
            // up (and entered) once per substance. A null store or a null property is a gap, never a guess — an
            // absent loading is not 1.0 (that under-orders an oxide).
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
    private async Task OnRevisionAsync(RevisionDoc r, CancellationToken ct)
    {
        // At-least-once change feed: only the first delivery acts. Marking the doc `applied` at the end
        // re-enters this handler once more, which is exactly what this guard absorbs.
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
            //    on an R.E. who already ruled. `what` keeps each arm's message; the persist closures still
            //    re-check (the mid-run race is theirs to catch).
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

            // 3 → 4. ORDER MATTERS between these two: void the gate BEFORE the new output lands. The persist
            //    is a change-feed event that re-enters TryAssembleAsync, and if that found the gate still
            //    `approved` it would mark Regulatory `done` over the new, unreviewed verdicts.
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
        var result = await agents.RunDiscoveryAsync(await LoadProjectAsync(r.ProjectId, ct), c, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the discovery agent could not apply the revision: {result.Error}");

        var candidates = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(candidates.Substances, Json.Options),
            // same id ⇒ replaces; the feed re-fans Regulatory over the new candidate set
            token => store.UpsertCandidatesAsync(candidates, token));
    }

    private async Task<RevisedStage> ReviseRegulatoryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var candidates = await store.GetCandidatesAsync(r.ProjectId, ct)
            ?? throw new InvalidOperationException("no candidates — Regulatory has not run for this project");
        var candidate = candidates.Substances.FirstOrDefault(s => s.Cas == r.Cas && s.ComponentId == r.ComponentId)
            ?? throw new InvalidOperationException(
                $"the revision targets {r.Cas}|{r.ComponentId}, which is not a candidate in this project");

        var result = await agents.RunRegulatoryAsync(c, candidate, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the regulatory agent could not apply the revision: {result.Error}");

        var verdict = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(verdict, Json.Options),
            // The agent's fresh VerdictDoc carries EvidenceReviewed=false and Determination=null by default,
            // so replacing the old one CLEARS the operator's prior ruling — deliberately. That ruling was
            // made against the verdict this one replaces; RegulatoryGate.Armable will now block the gate
            // until the operator opens this item again.
            token => store.UpsertVerdictAsync(verdict, token));
    }

    private async Task<RevisedStage> ReviseDosingAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // Re-resolve the SAME inputs the first run used — the compliant set, the measured floors, the
        // loadings — through the one shared resolver, so the revision path cannot relax what the first run
        // enforced. Validate fires again inside RunDosingAsync, so a directive that would dose below the floor
        // or reach outside the compliant set FAILS here, loudly, with the operator's reason still recorded as
        // a Learned Conclusion. The operator's directive is authoritative over the AGENT; it does not outrank
        // the regulatory gate.
        //
        // Re-check the signed gate BEFORE re-running, exactly as TryDoseAsync does on the first-run path.
        // A Regulatory revision can void the gate (VoidRegulatoryGateAsync locks it) or introduce an
        // unreviewed non-pass verdict since the signature; re-dosing behind a locked-or-uncovered gate would
        // regenerate dosing (and, per the Cost reset below, re-price) over an analysis the operator never
        // gated. CompliantSet + Validate already keep a rejected substance out of a code, so this is not a
        // regulatory false pass — but "Dosing runs only behind the signed gate" is the invariant the
        // first-run path enforces, and the revision path must not be the one hole in it. Throw so the
        // revision fails cleanly with the analysis untouched (the ordered-mutation contract of this method).
        //
        // (The closed-project refusal (Task 15(b)) ran before even that — hoisted into OnRevisionAsync,
        // one guard over all four arms. Once the VP gate is approved the project is history: nothing may
        // be re-dosed, and nothing re-priced, under a signed decision.)
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

        var result = await agents.RunDosingAsync(c, compliant, floors, loadings, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the dosing agent could not apply the revision: {result.Error}");

        var dosing = result.Output!;
        return new RevisedStage(
            JsonSerializer.Serialize(dosing, Json.Options),
            async token =>
            {
                // Re-check the close IMMEDIATELY before mutating (Task 15 review F1, layer 2 — the same
                // re-check ReviseDecisionAsync's closure runs, for the same race): the entry check passed
                // minutes ago, and a determination in flight then may have signed since. Without this, the
                // resets below plus the upsert would re-run the whole Cost→Decision cascade UNDERNEATH a
                // just-signed gate, regenerating the records the signature covers. Throw: the revision
                // lands honest `failed`, nothing reset, nothing persisted.
                await ThrowIfClosedAsync(c.ProjectId, "project", token);

                // A Dosing revision may change the codes' substance set, so a Cost audit computed over the
                // OLD set is now stale — the same "never leave an artifact that is wrong but looks current"
                // rule TryAssembleAsync applies to the Matrix. Reset Cost to `pending` so the persisted
                // DosingDoc re-triggers OnDosingAsync over the revised substances. A review note does NOT
                // travel this path, so the "a review note does not re-price" guard is preserved.
                await SetStageAsync(c.ProjectId, Stages.Cost,
                    s => { if (s.Status is "done" or "failed") { s.Status = "pending"; s.Error = null; } }, token);
                // ...and Decision with it (Task 15(a)): the DecisionDoc's rows and proposal were assembled
                // over the OLD dosing/cost, so a project parked `awaiting-VP` would otherwise keep a STALE
                // proposal at the VP's door — TryDecideAsync's status guard would ABSORB the fresh CostDoc.
                // Reset to `pending` (park error cleared) so the re-priced CostDoc IS the re-trigger and the
                // pick re-runs over the NEW dosing. `done` is deliberately EXCLUDED: done means the VP
                // signed and the project closed — history, which the refusal above keeps this path off
                // anyway (defense in depth).
                await SetStageAsync(c.ProjectId, Stages.Decision,
                    s => { if (s.Status is "awaiting-VP" or "needs-review" or "failed") { s.Status = "pending"; s.Error = null; } }, token);
                await store.UpsertDosingAsync(dosing, token);
            });
    }

    /// Revise-with-reason for the PICK (Task 15). Mirrors ReviseDosingAsync's shape: re-assemble from the
    /// LIVE records through the same deterministic fold the first run used (DecisionAssembler — the revise
    /// path cannot relax what the first run enforced), re-run the pick WITH the directive, and re-park at
    /// `awaiting-VP`. An unsigned/locked vp gate is left exactly as it stands: locked is already the safe
    /// state a void would produce, and nothing on this path may move a gate toward `approved` (Law 9).
    private async Task<RevisedStage> ReviseDecisionAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // The closed-project refusal (Task 15(b)) already ran — hoisted into OnRevisionAsync, before the
        // agent, before the conclusion, before anything that could look like progress. Decision `done`
        // means the VP SIGNED: a revision that rewrote the DecisionDoc now would put words under a
        // signature the VP never read.

        // Captured NOW so the persist closure can prove the stage did not move while the agent ran —
        // see the re-check below.
        var statusAtStart = (await store.GetProjectAsync(c.ProjectId, ct))
            ?.Stages.GetValueOrDefault(Stages.Decision)?.Status;

        var verdicts = await store.GetVerdictsAsync(c.ProjectId, ct);
        var dosing = await store.GetDosingAsync(c.ProjectId, ct)
            ?? throw new InvalidOperationException("no dosing on file — there are no finalized codes to re-pick over");
        var cost = await store.GetCostAsync(c.ProjectId, ct)
            ?? throw new InvalidOperationException("no cost audit on file — Decision has not run for this project");

        // Assemble may throw (the pre-invariant duplicate-window ArgumentException TryDecideAsync guards);
        // here the OnRevisionAsync catch turns that into an honestly-failed revision, analysis untouched.
        var assembled = DecisionAssembler.Assemble(
            verdicts, dosing, cost, [.. c.Components.Select(k => k.Id)]);

        var result = await agents.RunDecisionAsync(assembled, dosing, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the decision agent could not apply the revision: {result.Error}");

        var decision = result.Output!;
        decision.Id = RecordIds.Decision(c.ProjectId);
        decision.ProjectId = c.ProjectId;
        return new RevisedStage(
            JsonSerializer.Serialize(decision, Json.Options),
            async token =>
            {
                // Re-check the world IMMEDIATELY before writing (Task 15 review F1, layer 2). The run
                // between the entry checks and this line is minutes wide (two LLM calls, an embed, a
                // push), and the stage advertised `awaiting-VP` throughout — so a determination STARTED
                // before this revision landed can have completed mid-run (layer 3's pending-revision 422
                // blocks one started after). If the VP signed in that window, persisting would put an
                // unconfirmed doc OVER the stamped one — under an approved gate whose close then finds
                // zero confirmations. Throw instead: the revision lands honest `failed`, nothing written,
                // the signature survives.
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
                    s => { s.Status = "awaiting-VP"; s.Error = null; }, token);
            });
    }

    /// Task 15(b): an approved VP gate IS the project's close, and everything behind it is history — the
    /// Marker Library entry, the close conclusion, the released procurement all cite the SIGNED decision.
    /// Any revision that would regenerate an analytical record on a closed project is refused outright,
    /// before the agent runs and before anything is re-priced; revising history is a new project decision.
    private async Task ThrowIfClosedAsync(string projectId, string what, CancellationToken ct)
    {
        if ((await store.GetGateAsync(projectId, GateTypes.Vp, ct))?.Status == "approved")
            throw new InvalidOperationException(
                $"the project is closed — the VP signature is history; revising a closed {what} requires a new project");
    }

    /// A gate is an operator's signature over a SPECIFIC analysis, and the revision just replaced that
    /// analysis. Leaving the signature standing is the false pass: TryAssembleAsync will not lower a stage
    /// that already reached `done`, so an approved-and-done Regulatory stage would silently absorb verdicts
    /// the operator never reviewed. Void it and make them sign again.
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
            s => { if (s.Status == "done") s.Status = "awaiting-RE"; }, ct);
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
    private async Task OnChatMessageAsync(ChatMessageDoc fed, CancellationToken ct)
    {
        // RE-READ rather than trust the doc the feed handed us. The feed's element is a SNAPSHOT of the
        // message at some change, and the idempotency of this whole handler rests on the status being the
        // CURRENT one — a stale `pending` re-runs a turn that may already have queued a revision. Cosmos's
        // latest-version feed happens to hand back current content, but that is a property of the feed MODE,
        // not of this code: switch it to all-versions-and-deletes, replay an element off a log, or hand this
        // method an object held from earlier, and the snapshot is genuinely old. A partition-scoped point read
        // costs about one RU and removes the dependency on that mode altogether.
        //
        // It is also what makes the read-modify-write below sound: `m` is the store's own current doc, so
        // flipping its status writes back what is actually there instead of blind-overwriting Cosmos with a
        // stale payload. And a message the feed delivered that the store does not have cannot exist — if it
        // somehow does, upserting it would CONJURE a message nobody sent, so do nothing.
        if (await store.GetChatMessageAsync(fed.ProjectId, fed.Id, ct) is not { } m) return;

        // At-least-once feed: only the first delivery acts. A redelivered message must not re-run an agent
        // that may already have queued a revision. Answering is itself a change, so this handler is re-entered
        // on its own write — that too is what this guard absorbs.
        if (m.Status != ChatStatus.Pending) return;

        try
        {
            // PRIOR conversation only — the message being answered is excluded. It is already in the record by
            // the time this runs (the backend wrote it; that write IS the dispatch), so an unfiltered thread
            // would put it in "CONVERSATION SO FAR" and then repeat it under "THE OPERATOR'S NEW MESSAGE".
            //
            // The duplication is the lesser half. The real damage is on a FIRST turn: the agent would be shown
            // a one-message "conversation so far" instead of ChatThread.Render's "(there is no prior
            // conversation)" — which is an invitation to treat the operator's opening question as context it
            // has already dealt with, and to answer around a history it never had. That branch of Render was
            // written to prevent exactly this, and an unfiltered read here makes it unreachable.
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
            // writes below land. A crash in that window redelivers a still-`pending` message and the turn
            // RE-RUNS.
            //
            // GUARANTEED: an identical apply_revision call converges. ChatTools content-addresses the revision
            // id from (chat key + call content) and refuses to overwrite one that has already left `pending`,
            // so the same call cannot queue a second revision or file a second Learned Conclusion.
            // NOT GUARANTEED: that the replay makes the same call. It is a sampled model shown DIFFERENT
            // record state (the first revision may already be applied, the candidate already dropped), so it
            // may make a genuinely different one — a second revision out of one operator instruction. Closing
            // that requires the revision, the reply and the status flip to commit together (a Cosmos
            // transactional batch is possible — they share a partition key), which is a change to the write
            // path, not to this handler.
            await store.UpsertChatReplyAsync(new ChatReplyDoc
            {
                // Derived from the message's key, so redelivery upserts one reply instead of appending.
                Id = RecordIds.ChatReply(m.ProjectId, m.Stage, KeyOf(m.Id)),
                ProjectId = m.ProjectId, Stage = m.Stage, MessageId = m.Id,
                Text = text,
                // COPIED, not aliased: `Trail` is a live List the ChatTools instance still owns. ChatToolCall
                // is an immutable record, so a shallow copy is a complete one, and the reply's audit trail is
                // frozen at the instant the turn ended rather than tracking a list someone could still append to.
                ToolCalls = [.. chatTools.Trail],
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            m.Status = ChatStatus.Answered;
            m.Error = null;
            await store.UpsertChatMessageAsync(m, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The ORCHESTRATOR is stopping — the agent did not fail. `failed` is terminal (nothing re-runs a
            // failed message), so recording a shutdown as one would tell the operator permanently that their
            // question came back as "A task was canceled". Leave it `pending`: that is the truth — it was
            // never answered — and it is the only status a redelivery or a re-send can act on. Rethrow so the
            // worker logs a stop rather than swallowing it as an answered turn.
            //
            // The `when` filter is load-bearing: an OperationCanceledException NOT tied to our token is a real
            // failure (an HTTP timeout inside the model call surfaces as exactly this type) and belongs below.
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

    private async Task TryAssembleAsync(string projectId, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(candidates, verdicts)) return;

        // Defense in depth. The gate record carries no binding to the verdicts it was signed over, so an
        // `approved` status alone is not proof that the CURRENT analysis was reviewed. Re-check it: if a
        // revision (or a race with POST /approve landing between VoidRegulatoryGateAsync and the verdict
        // upsert) has introduced an unreviewed non-pass verdict since the signature, the stage must NOT go
        // `done`. VoidRegulatoryGateAsync is the primary guard; this is the one that holds when the ordering
        // doesn't — and a stage that reached `done` is never lowered again, so there is no second chance.
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        var stillArmable = RegulatoryGate.Armable(candidates, verdicts).Ok;
        var regStatus = gate?.Status == "approved" && stillArmable ? "done" : "awaiting-RE";
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }, ct);

        // Always re-assemble. The old `if (matrix is null)` guard left the matrix STALE after a revise: it
        // kept showing the tiers and verdicts the revision had replaced, and a compliance artifact that is
        // wrong but looks current is exactly what this system must never produce. Assemble is pure over
        // (candidates, verdicts) so re-writing is idempotent, and the MatrixDoc change-feed branch is
        // terminal (`case MatrixDoc: break;`), so this cannot loop.
        var componentIds = constraints.Components.Select(k => k.Id).ToList();
        await store.UpsertMatrixAsync(
            MatrixAssembler.Assemble(candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
        await SetStageAsync(projectId, Stages.Matrix, s => s.Status = "done", ct);
    }

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
        if (await store.GetProjectAsync(projectId, ct) is not { } p) return;
        mutate(p.Stages[stage]);
        await store.UpsertProjectAsync(p, ct);
    }
}
