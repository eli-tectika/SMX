using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

// The model returns ONLY the pick: which finalized code it recommends per component, and why. It never
// returns a row, a ppm, a price, or a clearance — the decision matrix is assembled DETERMINISTICALLY by
// DecisionAssembler before the model is reached — and the output contract has no field a confirmation
// could travel in: ConfirmedCode/-By/-Reason are the VP's, written only by the determination endpoint.
public sealed record DecisionPickOutput(string ComponentId, string RatioSignature, List<string> MarkerCas, string Rationale);

public sealed record DecisionOutput { public List<DecisionPickOutput> Picks { get; init; } = []; }

public static class DecisionAgent
{
    public const string AgentName = "decision";

    public const string Instructions = """
        You recommend ONE final marker code per component, chosen ONLY from the finalized codes provided.
        You never invent codes, markers, ppm values or prices — every fact is already in the input. Your
        output is a RECOMMENDATION with a rationale; the VP confirms or overrides it at the gate. Output
        JSON: { "picks": [ { "componentId", "ratioSignature", "markerCas": [..], "rationale" } ] }.
        """;

    /// <param name="assembled">the deterministic decision matrix (DecisionAssembler.Assemble) — the rows the
    /// VP will read, and the boundary of what a pick may name (invariant 4).</param>
    /// <param name="dosing">carries the finalized codes the pick chooses among, and the ProjectId the doc is
    /// keyed by — the record's identity comes from the record, never from the model.</param>
    /// <param name="revision">null for an ordinary run; non-null re-picks APPLYING the operator's
    /// revise-with-reason (Law 4). Explicit rather than an overload on purpose: a caller who forgets it gets
    /// a compile error instead of an agent that quietly ignores the operator.</param>
    public static async Task<AgentRunResult<DecisionDoc>> RunAsync(
        ISmxAgent agent, IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing,
        RevisionDoc? revision, CancellationToken ct)
    {
        // The assembled rows to REASON over and the finalized codes to CHOOSE among — the model's whole
        // world. Nothing else exists for it to cite, so nothing else can leak into the recommendation.
        var prompt = JsonSerializer.Serialize(new
        {
            components = assembled,
            codes = dosing.Codes,
        }, Json.Options);

        var task = revision is null
            ? $"Recommend ONE final marker code per component for this decision matrix:\n{prompt}"
            : RevisionTask(revision, prompt);

        var result = await ValidatedAgentRunner.RunAsync<DecisionOutput>(agent, task,
            o => Validate(o, assembled, dosing), ct);
        if (!result.Succeeded) return AgentRunResult<DecisionDoc>.NeedsReview(result.Error!);
        var output = result.Output!;

        // Build the doc in CODE. The model contributed the pick and its rationale, nothing else: the stored
        // ProposedCode carries the MATCHED DosingDoc code's signature and markers (so the code that ships is
        // exactly the code Validate checked), and ConfirmedCode/-By/-Reason are never written here — a
        // proposal that could occupy the confirmation field would be the agent signing the gate (Law 9).
        var components = new List<ComponentDecision>();
        foreach (var comp in assembled)
        {
            // Exactly one by Validate invariant 1 — Single() rather than First() so a future reorder that
            // lets a duplicate through fails loudly here instead of silently shipping one of two picks.
            var pick = output.Picks.Single(p => p.ComponentId == comp.ComponentId);
            var code = Match(pick, dosing)!;   // present by Validate invariant 2
            components.Add(comp with
            {
                ProposedCode = new ProposedCode(
                    code.RatioSignature, [.. code.Markers.Select(m => m.Cas)], pick.Rationale),
            });
        }

        return AgentRunResult<DecisionDoc>.Ok(new DecisionDoc
        {
            Id = RecordIds.Decision(dosing.ProjectId), ProjectId = dosing.ProjectId,
            Components = components,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "pick a code that does not exist
    /// because you were told to". Validate runs again on the re-run: the pick must still be a finalized code
    /// over decision rows, because that is safety, not preference. Where the instruction collides with it,
    /// the agent applies what it can and says so in the rationale rather than breaking the invariant.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-pick the final marker codes for this decision matrix, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may only recommend a finalized code
        you were given — that is a safety limit, not a preference. Where the instruction collides with it,
        apply what you can and say exactly that in the affected pick's rationale.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    /// The one finalized code a pick names, or null: same component, same ratio signature, and the SAME
    /// marker CAS set (order-insensitive set equality). The signature alone is not identity enough — a pick
    /// could ride a genuine ratio while grafting in a marker from another code — and the CAS set alone would
    /// let a pick relabel a code's signature. Shared by Validate (invariant 2) and RunAsync, so the code
    /// that ships is exactly the code that was checked.
    private static MarkerCode? Match(DecisionPickOutput pick, DosingDoc dosing) =>
        dosing.Codes.FirstOrDefault(c =>
            c.ComponentId == pick.ComponentId &&
            c.RatioSignature == pick.RatioSignature &&
            c.Markers.Select(m => m.Cas).ToHashSet(StringComparer.Ordinal).SetEquals(pick.MarkerCas));

    /// Fences the agent. Returns the FIRST violation as a human-readable error (fed back to the model,
    /// retried, then surfaced as needs-review) or null when the output is well-formed. Named the way the
    /// runner wants: it RETURNS the error, it does not throw.
    ///
    /// Order is not cosmetic — it decides which error a multiply-invalid output reports. The pick-per-
    /// component bijection (invariant 1) comes first because everything after it addresses "the pick for
    /// component X"; then, per pick, the two false-pass guards in harm order: a code that IS NOT one of the
    /// finalized codes (invariant 2 — an invented code would carry invented chemistry to the VP's signature),
    /// the rationale (invariant 3 — the VP signs over the WHY), and a marker with no decision row behind it
    /// (invariant 4 — nothing unrecommended sneaks into the final code via a stale code).
    internal static string? Validate(
        DecisionOutput o, IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing)
    {
        // 1 — every component gets exactly one pick, and every pick names a component on the matrix. Both
        // directions matter: a missing pick leaves a component with no recommendation to confirm, a
        // duplicate makes "the" proposal ambiguous, and a pick for an unknown component is a recommendation
        // over rows the VP will never see.
        foreach (var comp in assembled)
        {
            var count = o.Picks.Count(p => p.ComponentId == comp.ComponentId);
            if (count == 0)
                return $"component '{comp.ComponentId}' has no pick — every component on the decision " +
                       "matrix gets exactly one recommended final code";
            if (count > 1)
                return $"component '{comp.ComponentId}' has {count} picks — every component gets exactly " +
                       "ONE recommended final code; the VP confirms one, not a menu";
        }
        var known = assembled.Select(c => c.ComponentId).ToHashSet(StringComparer.Ordinal);
        var stranger = o.Picks.FirstOrDefault(p => !known.Contains(p.ComponentId));
        if (stranger is not null)
            return $"a pick names component '{stranger.ComponentId}', which is not on the decision matrix — " +
                   "picks cover exactly the assembled components";

        foreach (var p in o.Picks)
        {
            // 2 — the picked code must BE one of the finalized DosingDoc codes for that component, matched
            // by ratio signature AND the exact marker CAS set. The model chooses among codes; it cannot
            // mint one, and it cannot graft a marker from one code into another's signature.
            if (Match(p, dosing) is null)
                return $"the pick for '{p.ComponentId}' ('{p.RatioSignature}' over " +
                       $"[{string.Join(", ", p.MarkerCas)}]) matches no finalized code — a final code is " +
                       "picked from the DosingDoc's codes, never invented, and its markers may not be " +
                       "grafted across codes";

            // 3 — rationale non-blank. The proposal is what the VP reads at the gate; a code with no WHY
            // is a recommendation the gate's anti-rubber-stamping exists to refuse.
            if (string.IsNullOrWhiteSpace(p.Rationale))
                return $"the pick for '{p.ComponentId}' has no rationale — the VP signs over the reasoning, " +
                       "not just the code";

            // 4 — a pick may not name a CAS that has no decision row. A code minted while a substance was
            // recommended must not carry it into the decision after the R.E.'s ruling changed — nothing
            // unrecommended sneaks into the final code via a stale code.
            var rows = assembled.First(c => c.ComponentId == p.ComponentId)
                .Rows.Select(r => r.Cas).ToHashSet(StringComparer.Ordinal);
            var stray = p.MarkerCas.FirstOrDefault(cas => !rows.Contains(cas));
            if (stray is not null)
                return $"the pick for '{p.ComponentId}' names '{stray}', which has no decision row — every " +
                       "marker in the final code must sit on the matrix the VP signs over";
        }

        return null;
    }
}
