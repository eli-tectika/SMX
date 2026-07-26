using Smx.Domain.Records;

namespace Smx.Eval;

public static class EvalMetrics
{
    public static EvalReport Score(IReadOnlyList<ExpectedCell> expected, IReadOnlyList<MatrixCell> actual)
    {
        var report = new EvalReport();
        var byCell = actual.ToDictionary(c => (c.Cas, c.ComponentId));
        foreach (var e in expected)
        {
            var track = report.Tracks.TryGetValue(e.Track, out var t) ? t : report.Tracks[e.Track] = new TrackScore();
            track.Total++;

            if (!byCell.TryGetValue((e.Cas, e.ComponentId), out var cell))
            {
                report.MissingCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: no cell in matrix (expected {e.Expected})");
                continue;
            }
            var uncited = cell.Dimensions.Any(d => d.Citations.Count == 0);
            if (uncited)
            {
                report.UncitedCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: uncited dimension — counts as failure");
            }
            var agreed = cell.Overall == e.Expected && !uncited;
            if (agreed) track.Agreed++;
            else if (cell.Overall != e.Expected)
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: expected {e.Expected}, got {cell.Overall}");
            // the harm metric: model said usable where the golden answer is Fail
            if (e.Expected == VerdictStatus.Fail && cell.Overall is VerdictStatus.Pass or VerdictStatus.Conditional)
                report.FalsePassCount++;
        }
        return report;
    }

    /// The design-§9 DOSING invariants, scored into <paramref name="report"/>. Every breach is a HARM case —
    /// a marker nobody can read, a "code" that is not one, or a marker outside the compliant set — so each
    /// violation counts as a FALSE PASS and trips the harness's non-zero exit, exactly like a matrix
    /// false-pass. It is not folded into the agreement tracks: there is no golden answer to agree with here,
    /// only invariants that must hold over whatever the agent produced.
    public static void ScoreDosing(DosingDoc dosing, EvalReport report)
    {
        // 1. floor < recommended < upper, strictly, for every window. At or below the floor is a marker the
        //    deployment device cannot read in the field; at or above the upper is a dose past the estimate
        //    the window itself declared. Nothing downstream re-checks either.
        foreach (var w in dosing.Windows)
            if (!(w.Floor.Ppm < w.RecommendedPpm && w.RecommendedPpm < w.Upper.Ppm))
            {
                report.FalsePassCount++;
                report.Failures.Add($"dosing: {w.Cas}×{w.ComponentId} recommended {w.RecommendedPpm} ppm " +
                                    $"is outside its window ({w.Floor.Ppm}, {w.Upper.Ppm})");
            }

        // Windows are built ONLY over the compliant set, so a marker CAS with no window is a marker outside
        // the compliant set — the self-contained form of "every marker is in the compliant set".
        var windowed = dosing.Windows.Select(w => w.Cas).ToHashSet(StringComparer.Ordinal);
        foreach (var code in dosing.Codes)
        {
            // 2. A code is 2–3 markers identified by their ppm RATIO: one marker has no ratio, four is not
            //    a code this system defines.
            if (code.Markers.Count is not (>= 2 and <= 3))
            {
                report.FalsePassCount++;
                report.Failures.Add($"dosing: code in '{code.ComponentId}' has {code.Markers.Count} " +
                                    "marker(s) — a code is 2–3 markers");
            }

            // 3. Every code-marker's CAS has a corresponding ppm window.
            foreach (var m in code.Markers.Where(m => !windowed.Contains(m.Cas)))
            {
                report.FalsePassCount++;
                report.Failures.Add($"dosing: code marker {m.Cas} in '{code.ComponentId}' has no ppm window " +
                                    "— a marker outside the compliant set");
            }
        }
    }

    /// The Plan-5 DECISION invariants, same contract as <see cref="ScoreDosing"/>: invariants only, each
    /// breach a FALSE PASS (non-zero exit), self-contained from the fetched docs. These audit the SIGNED
    /// state — a ConfirmedCode that names no real code, an order outside the signature, a signature over an
    /// uncleared row — because after the close nothing downstream re-checks any of them.
    public static void ScoreDecision(DecisionDoc decision, DosingDoc dosing, EvalReport report)
    {
        foreach (var c in decision.Components)
        {
            if (c.ConfirmedCode is null) continue; // unsigned components make no claims to audit

            // 2. The signed code must EXIST among the DosingDoc's finalized codes for that component —
            //    a signed nonexistent code is a signature nothing can trace, dose, or order.
            if (!dosing.Codes.Any(k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode))
            {
                report.FalsePassCount++;
                report.Failures.Add($"decision: signed code '{c.ConfirmedCode}' does not exist in " +
                                    $"'{c.ComponentId}' — a signature over a nonexistent code");
            }

            // 4. A ConfirmedCode present while a row is not regulatory-cleared is a signature over an
            //    uncleared row — the harm case, verbatim.
            foreach (var r in c.Rows.Where(r => !r.Cleared.Regulatory))
            {
                report.FalsePassCount++;
                report.Failures.Add($"decision: '{c.ComponentId}' is signed while {r.Cas} shows an " +
                                    "uncleared regulatory criterion — a signature over an uncleared row");
            }
        }

        // 3. Released procurement: every ordered CAS must be a marker of a CONFIRMED code — you cannot
        //    have bought what the VP did not sign.
        if (decision.Procurement.Status == ProcurementStatus.Released)
        {
            var signed = decision.Components
                .Where(c => c.ConfirmedCode is not null)
                .SelectMany(c => dosing.Codes.Where(
                    k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode))
                .SelectMany(k => k.Markers).Select(m => m.Cas).ToHashSet(StringComparer.Ordinal);
            foreach (var cas in decision.Procurement.OrderedCas.Where(cas => !signed.Contains(cas)))
            {
                report.FalsePassCount++;
                report.Failures.Add($"decision: ordered '{cas}' is not a marker in any VP-confirmed code " +
                                    "— an order outside the signature");
            }
        }
    }
}
