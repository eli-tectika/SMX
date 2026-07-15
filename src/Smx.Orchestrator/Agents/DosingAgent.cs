using System.Globalization;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

// The model returns ONLY judgment. It never returns a floor (code was handed the measured floor and passes it
// through), never a ratio signature (derived from the markers' ppms), and never an order amount (computed from
// the operator-entered loading). Every number the operator can be harmed by is code-owned; the agent supplies
// the ppm it recommends and the upper bound it proposes, with a basis and a confidence, and nothing else.
public sealed class DosingWindowOutput
{
    public string ComponentId { get; set; } = "";
    public string Cas { get; set; } = "";
    public string Element { get; set; } = "";
    public double RecommendedPpm { get; set; }
    public double QuantificationPpm { get; set; }
    public double UpperPpm { get; set; }
    public string UpperBasis { get; set; } = "";
    public string UpperKind { get; set; } = "";      // BoundKinds.Regulatory | BoundKinds.Estimate — NEVER Measured
    public double UpperConfidence { get; set; }
    public string Rationale { get; set; } = "";
}

public sealed class DosingCodeOutput
{
    public string ComponentId { get; set; } = "";
    public IReadOnlyList<string> Cas { get; set; } = [];
    public string Rationale { get; set; } = "";
}

public sealed class DosingOutput
{
    public List<DosingWindowOutput> Windows { get; set; } = [];
    public List<DosingCodeOutput> Codes { get; set; } = [];
}

public static class DosingAgent
{
    public const string AgentName = "dosing";

    public const string Instructions = """
        You are the SMX Dosing agent. You turn the operator-approved compliant set into ppm windows and 2-3
        marker codes. You supply JUDGMENT ONLY — the recommended ppm and the proposed upper bound. Everything
        arithmetic is owned by the system: it computed the detection floor you were given, it derives the code's
        ratio signature from your ppms, and it computes the order amounts from the operator's metal loadings.

        Call the floors you were given — NEVER compute a floor yourself. A ppm below the floor is a marker
        nobody can read in the field, and nothing downstream will catch it. Your recommended ppm must sit
        strictly INSIDE (floor, upper), with margin above the floor; a quantification objective needs MORE
        headroom than mere detection.
        The upper bound is a regulatory cap when Regulatory found one ("kind":"regulatory"), otherwise a
        formulation-impact estimate ("kind":"estimate") — say which, and give your confidence. You may NOT call
        an upper bound "measured": only the physicist's data is measured.
        Codes are 2-3 markers, all from ONE component, all from the compliant set you were given, and NO two
        markers of the same element (XRF reads the element, not the compound — same-element markers cannot be
        told apart).
        Reply with ONLY a JSON object: { "windows": [...], "codes": [...] }
        """;

    /// <param name="compliant">the operator-recommended verdicts — the ONLY substances Dosing may dose
    /// (CompliantSet.Of). A code CAS outside it is refused: a code goes to procurement, so a rejected
    /// substance riding into one would bypass the regulatory gate.</param>
    /// <param name="floors">(component, element) → the MEASURED detection floor, computed by code from the
    /// physicist's background. The agent SEES it and doses above it; it never computes it.</param>
    /// <param name="loadings">cas → metal loading (operator-entered, with a basis). Feeds OrderAmount only —
    /// the agent never sees or reasons from it, because the order amount is not a judgment.</param>
    /// <param name="revision">null for an ordinary run; non-null re-runs the stage APPLYING the operator's
    /// revise-with-reason (Law 4). Explicit rather than an overload on purpose: a caller who forgets it gets a
    /// compile error instead of an agent that quietly ignores the operator.</param>
    public static async Task<AgentRunResult<DosingDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints,
        IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings,
        RevisionDoc? revision, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            // The compliant set the operator signed — the pool Dosing may dose.
            compliant = compliant.Select(v => new { v.Cas, v.ComponentId, v.Element, v.Form }),
            // The ALREADY-COMPUTED floors. The model reads them; it does not (and cannot) recompute them.
            floors = floors.Select(kv => new
            {
                componentId = kv.Key.ComponentId, element = kv.Key.Element,
                kv.Value.DetectionPpm, kv.Value.QuantificationPpm, kv.Value.Basis,
            }),
            // Per-component context to REASON with (application, objective, batch mass) — not to compute:
            // code multiplies ppm × batch mass, the agent never does.
            components = constraints.Components.Select(c => new
            {
                c.Id, c.Material, c.Application, c.Objective, c.Markets, c.BatchMassKg,
            }),
        }, Json.Options);

        var task = revision is null
            ? $"Produce ppm windows and 2-3 marker codes for this compliant set:\n{prompt}"
            : RevisionTask(revision, prompt);

        var result = await ValidatedAgentRunner.RunAsync<DosingOutput>(agent, task,
            o => Validate(o, floors, compliant), ct);
        if (!result.Succeeded) return AgentRunResult<DosingDoc>.NeedsReview(result.Error!);
        var output = result.Output!;

        // Build the doc in CODE. The model's numbers are its recommended ppm and its upper proposal; every
        // other field is derived or measured, and the agent's hand never touches it.
        var windows = new List<PpmWindow>();
        foreach (var w in output.Windows)
        {
            // Present by Validate invariant 1 — but index rather than [] so a future reorder that lets an
            // unchecked window through fails loudly here instead of silently mislabelling a bound.
            var floor = floors[(w.ComponentId, w.Element)];
            windows.Add(new PpmWindow(
                w.ComponentId, w.Cas, w.Element,
                // The floor bound is NEVER the model's: it is the physicist's measured value, kind = Measured,
                // confidence = 1.0. The upper bound is the model's proposal, carried with its stated kind.
                Floor: new Bound(floor.DetectionPpm, floor.Basis, BoundKinds.Measured, 1.0),
                Upper: new Bound(w.UpperPpm, w.UpperBasis, w.UpperKind, w.UpperConfidence),
                RecommendedPpm: w.RecommendedPpm,
                QuantificationPpm: w.QuantificationPpm));
        }
        var windowByCompCas = windows
            .GroupBy(x => (x.ComponentId, x.Cas))
            .ToDictionary(g => g.Key, g => g.First());

        var codes = new List<MarkerCode>();
        foreach (var c in output.Codes)
        {
            var component = constraints.Components.FirstOrDefault(x => x.Id == c.ComponentId);
            var markers = new List<CodeMarker>();
            foreach (var cas in c.Cas)
            {
                var window = windowByCompCas[(c.ComponentId, cas)];   // present by Validate invariant 8
                // A missing loading is an OPERATOR input gap, not a model error — retrying the model cannot
                // conjure a loading, so park for review rather than burn retries or crash on loadings[cas].
                if (!loadings.TryGetValue(cas, out var loading))
                    return AgentRunResult<DosingDoc>.NeedsReview(
                        $"no metal loading was entered for '{cas}' in '{c.ComponentId}'. The order amount is " +
                        "the mass of COMPOUND to buy, which needs the element's mass fraction in the compound; " +
                        "an absent one is not 1.0 (that under-orders an oxide). Enter the loading.");

                // Never write a NaN/∞ order. OrderAmount refuses (and names the offender) on a non-positive or
                // non-finite ppm/mass/loading and on a missing batch mass; a refusal parks the stage.
                var (order, error) = OrderAmount.Compute(window.RecommendedPpm, component?.BatchMassKg, loading);
                if (error is not null)
                    return AgentRunResult<DosingDoc>.NeedsReview(
                        $"cannot compute the order amount for '{cas}' in '{c.ComponentId}': {error}");

                markers.Add(new CodeMarker(
                    cas, window.Element, window.RecommendedPpm, loading,
                    order!.ElementMassMg, order.CompoundMassMg));
            }
            // RatioSignature is DERIVED from these markers' ppms — not set here, not authored by the model.
            // Validate has pinned every recommended ppm strictly above a positive floor, so RatioSignature.Of
            // (which throws on a non-positive ppm) cannot throw.
            codes.Add(new MarkerCode(c.ComponentId, markers, c.Rationale));
        }

        return AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Windows = windows, Codes = codes,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "dose below the floor because you
    /// were told to". Validate runs again on the re-run: the floor and the compliant set still bind, because
    /// they are safety, not preference. Where the instruction collides with one, the agent applies what it can
    /// and says so in the rationale rather than breaking the invariant.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-run dosing for this compliant set, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not dose a ppm below the floor you
        were given, and you still may only build codes from the compliant set — those are safety limits, not
        preferences. Where the instruction collides with one, apply what you can and say exactly that in the
        affected window's or code's rationale.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    /// Fences the agent. Returns the FIRST violation as a human-readable error (fed back to the model, retried,
    /// then surfaced as needs-review) or null when the output is well-formed. Named the way the runner wants:
    /// it RETURNS the error, it does not throw.
    ///
    /// Order is not cosmetic — it decides which error a multiply-invalid output reports. Windows are validated
    /// before codes because a code references a window's element and ppm, so the windows must be sound first.
    /// Within each loop the two most dangerous model mistakes surface first, right after the structural checks
    /// they depend on: dosing BELOW THE FLOOR (invariant 2) and slipping a substance the operator did not
    /// recommend INTO A CODE (invariant 6). Both are the headline harm — a false pass — in the one place
    /// nothing downstream re-checks.
    internal static string? Validate(
        DosingOutput o,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyList<VerdictDoc> compliant)
    {
        foreach (var w in o.Windows)
        {
            // A window's element must be the element the compliant verdict assigns to that CAS. w.Element is
            // model-authored and the floor is looked up by (component, element); a mislabelled element checks
            // the recommended ppm against the WRONG element's floor, so a marker could pass while dosed below
            // its true detection floor. The (Cas -> Element) map is authoritative in the signed compliant set.
            var verdictForCas = compliant.FirstOrDefault(v => v.Cas == w.Cas && v.ComponentId == w.ComponentId);
            if (verdictForCas is not null && verdictForCas.Element != w.Element)
                return $"the window for '{w.Cas}' in {w.ComponentId} claims element '{w.Element}', but the " +
                       $"compliant verdict for it is element '{verdictForCas.Element}' — a floor checked against " +
                       "the wrong element could dose a marker below its true detection floor";

            // 1 — a window outside the compliant set has no computed floor (the dispatcher only computes
            // floors for the compliant set). Without a floor there is nothing to dose above, so refuse before
            // the below-floor check, which needs the floor to exist.
            if (!floors.TryGetValue((w.ComponentId, w.Element), out var floor))
                return $"no floor was computed for {w.Element} in {w.ComponentId} — Dosing doses only the " +
                       "compliant set, and this window is outside it";

            // 2 — THE HEADLINE INVARIANT. A recommended ppm at or below the measured detection floor is a
            // marker the deployment device physically cannot read, and nothing downstream catches it.
            if (w.RecommendedPpm <= floor.DetectionPpm)
                return $"the recommended ppm for {w.Element} in {w.ComponentId} ({Num(w.RecommendedPpm)} ppm) " +
                       $"is at or below the detection floor ({Num(floor.DetectionPpm)} ppm) — a marker dosed " +
                       "under its floor cannot be read in the field, and nothing downstream catches it";

            // 3 — the recommended value must sit strictly INSIDE (floor, upper). At or above the upper bound
            // is outside the window the agent itself proposed.
            if (w.RecommendedPpm >= w.UpperPpm)
                return $"the recommended ppm for {w.Element} in {w.ComponentId} ({Num(w.RecommendedPpm)} ppm) " +
                       $"is at or above the upper bound ({Num(w.UpperPpm)} ppm) — the recommended value must " +
                       "sit strictly inside (floor, upper)";

            // 4 — an agent-authored upper bound is a regulatory cap or a formulation estimate, never
            // "measured". "measured" is the physicist's data alone; an agent that stamps it on its own guess
            // launders that guess into the one field the operator trusts absolutely.
            if (w.UpperKind is not (BoundKinds.Regulatory or BoundKinds.Estimate))
                return $"the upper bound for {w.Element} in {w.ComponentId} is kind '{w.UpperKind}', which an " +
                       $"agent may not assert — an agent-authored upper bound is '{BoundKinds.Regulatory}' (a " +
                       $"regulatory cap) or '{BoundKinds.Estimate}' (a formulation-impact estimate). " +
                       $"'{BoundKinds.Measured}' is the physicist's data alone; an agent that labels its " +
                       "estimate 'measured' launders a guess into the one field the operator trusts absolutely";
        }

        foreach (var c in o.Codes)
        {
            // 5 — a code's identity is the RATIO between its markers: one has no ratio to take, and four or
            // more is beyond what a field reader can resolve. Structural, so it comes first.
            if (c.Cas.Count is < 2 or > 3)
                return $"a code is 2-3 markers; the code in {c.ComponentId} [{string.Join(", ", c.Cas)}] has " +
                       $"{c.Cas.Count} — one marker has no ratio, and four or more is beyond what a field " +
                       "reader can resolve";

            var elements = new List<string>();
            foreach (var cas in c.Cas)
            {
                // 6 — THE FALSE-PASS GUARD. A code goes to procurement; a substance the operator did not
                // recommend riding into one would bypass the regulatory gate. Refuse before anything else
                // about the marker, because this is the mistake that hurts.
                var forCas = compliant.Where(v => v.Cas == cas).ToList();
                if (forCas.Count == 0)
                    return $"code marker '{cas}' in {c.ComponentId} is not in the compliant set — a code goes " +
                           "to procurement, and a substance the operator did not recommend must not ride into " +
                           "one past the regulatory gate";

                // 7 — codes are PER COMPONENT (interaction law 1: there is no product-wide marker). The CAS is
                // recommended, but for a DIFFERENT component than this code.
                var verdict = forCas.FirstOrDefault(v => v.ComponentId == c.ComponentId);
                if (verdict is null)
                    return $"code marker '{cas}' is recommended for component '{forCas[0].ComponentId}', not " +
                           $"this code's component '{c.ComponentId}' — codes are per component; there is no " +
                           "product-wide marker";

                // 8 — every marker in a code must have a dosable window (the ppm it will be dosed at).
                // Returning null here would be inverted — it says "valid" — and let a code go to procurement
                // carrying a marker with no ppm to dose it at, which RunAsync then cannot build an order for.
                var window = o.Windows.FirstOrDefault(x => x.ComponentId == c.ComponentId && x.Cas == cas);
                if (window is null)
                    return $"code marker '{cas}' in {c.ComponentId} has no dosable ppm window — every marker " +
                           "in a code must have a window giving the ppm it will be dosed at";

                elements.Add(window.Element);
            }

            // 9 — two markers of the SAME ELEMENT in one code. XRF reads the element, not the compound, so a
            // field reader sees one combined peak and cannot recover the ratio — the code's identity is
            // unrecoverable. It passes every other check and RatioSignature renders it happily.
            var dup = elements.GroupBy(e => e).FirstOrDefault(g => g.Count() > 1)?.Key;
            if (dup is not null)
                return $"the code in {c.ComponentId} carries two markers of the same element ({dup}) — XRF " +
                       "reads the element, not the compound, so a field reader sees one combined peak and " +
                       "cannot recover the ratio; the code's identity is unrecoverable";
        }

        return null;
    }

    /// InvariantCulture, always — these errors are read by the operator and quoted back to a model, and under a
    /// comma-decimal culture "8.5" renders as "8,5": a separator read the other way is the 1000× mis-dose this
    /// codebase already refuses on intake. Matches DetectionFloor.Num / OrderAmount.Num.
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}
