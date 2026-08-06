using Smx.Domain.Records;

namespace Smx.Domain;

/// What Discovery found out about a candidate.
public sealed record DiscoveryCells(string Tier, bool Preferred, string Rationale, int Sources);

/// What Regulatory found, and who said so.
///
/// <paramref name="ProposedDetermination"/> and <paramref name="Determination"/> are SEPARATE fields on the
/// wire, exactly as they are on the record. Collapsing them into one column would be the agent signing the
/// regulatory gate — the thing <see cref="CompliantSet"/> exists to prevent — reintroduced at the rendering
/// layer, where nobody would think to look for it.
public sealed record RegulatoryCells(
    VerdictStatus Overall, IReadOnlyList<DimensionVerdict> Dimensions,
    string? ProposedDetermination, string? Determination, bool EvidenceReviewed);

/// The dosable window and what it costs to buy. Both <see cref="Bound"/>s travel WHOLE — a ppm without its
/// `Kind` is a number whose provenance has been thrown away, and a measured floor and an estimated one are
/// not the same claim.
public sealed record DosingCells(
    Bound Floor, Bound Upper, double RecommendedPpm, double CompoundMassMg,
    IReadOnlyList<string> Suppliers, IReadOnlyList<string> Risks);

/// Where the substance ended up.
public sealed record OutcomeCells(string? InCode, bool Ordered);

/// One row of the project table: one substance in one component, with each phase's contribution as a
/// separate, NULLABLE group.
///
/// A null group means the phase produced nothing for this row — and <see cref="StoppedAt"/> is what says
/// WHY. See <see cref="ProjectTable"/> for the distinction that matters.
public sealed record TableRow(
    string ComponentId, string Cas, string Element, string Form,
    DiscoveryCells? Discovery, RegulatoryCells? Regulatory,
    DosingCells? Dosing, OutcomeCells? Outcome,
    string? StoppedAt, string? StoppedReason);

/// The whole project record as ONE table, keyed on (component, CAS).
///
/// Every record from Discovery onward already shares that key — candidates, verdicts, matrix cells, ppm
/// windows, decision rows. The record has always BEEN one wide table; it was rendered as five unrelated
/// screens that each fetched a slice. This does the join once, server-side, so the UI and the XLSX export
/// cannot disagree about what the record says.
///
/// THE DISTINCTION THIS TYPE EXISTS TO PRESERVE: an empty cell can mean two completely different things —
/// *this row was dropped here* or *this phase has not run yet* — and they must never render alike. That
/// conflation is the bug family this codebase has already shipped four times (whatsBlocking with no
/// awaiting-VP branch, foldStatus swallowing every park into pending, isTerminal sharing the flaw). So
/// Build is handed the STAGE STATUSES, and a row is only <see cref="TableRow.StoppedAt"/> a phase that has
/// actually run. Where a phase has not run, the group is null and StoppedAt stays null: "not reached".
public static class ProjectTable
{
    public static IReadOnlyList<TableRow> Build(
        CandidatesDoc? candidates,
        IReadOnlyList<VerdictDoc> verdicts,
        DosingDoc? dosing,
        DecisionDoc? decision,
        IReadOnlyDictionary<string, StageState> stages)
    {
        // No candidates ⇒ no rows. Not an error: Discovery has not produced anything yet, and inventing
        // rows from verdicts alone would resurrect substances a revise deliberately orphaned.
        if (candidates is null) return [];

        var hasRun = (string stage) =>
            stages.TryGetValue(stage, out var s) && s.Status == StageStatus.Done;

        var regulatoryRan = hasRun(Stages.Regulatory);
        var dosingRan = hasRun(Stages.Dosing);
        var decisionRan = hasRun(Stages.Decision);

        var verdictBy = verdicts
            .GroupBy(v => (v.ComponentId, v.Cas))
            .ToDictionary(g => g.Key, g => g.First());
        var windowBy = (dosing?.Windows ?? [])
            .GroupBy(w => (w.ComponentId, w.Cas))
            .ToDictionary(g => g.Key, g => g.First());
        var supplyBy = (dosing?.Supply ?? [])
            .GroupBy(a => a.Cas, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var markerBy = (dosing?.Codes ?? [])
            .SelectMany(k => k.Markers.Select(m => (Key: (k.ComponentId, m.Cas), Marker: m, Code: k)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.First());

        var confirmedBy = (decision?.Components ?? [])
            .Where(c => c.ConfirmedCode is not null)
            .ToDictionary(c => c.ComponentId, c => c.ConfirmedCode!);
        var ordered = new HashSet<string>(
            decision?.Procurement.OrderedCas ?? [], StringComparer.OrdinalIgnoreCase);

        var rows = new List<TableRow>();
        foreach (var s in candidates.Substances
                     .OrderBy(s => s.ComponentId, StringComparer.Ordinal)
                     .ThenBy(s => s.Cas, StringComparer.Ordinal))
        {
            var key = (s.ComponentId, s.Cas);

            var discovery = new DiscoveryCells(s.Tier, s.Preferred, s.Rationale, s.Citations.Count);

            RegulatoryCells? regulatory = null;
            string? stoppedAt = null, stoppedReason = null;

            if (verdictBy.TryGetValue(key, out var v))
            {
                regulatory = new RegulatoryCells(
                    v.Overall, v.Dimensions, v.ProposedDetermination, v.Determination, v.EvidenceReviewed);

                // A REJECTION BY THE OPERATOR stops the row. A proposed rejection does NOT: the operator may
                // still overrule it, and rendering the agent's opinion as a decision is the Law-9 line
                // reappearing at the presentation layer.
                if (v.Determination == Determinations.Rejected)
                {
                    stoppedAt = Stages.Regulatory;
                    stoppedReason = v.DeterminationReason ?? "rejected by the operator";
                }
            }

            DosingCells? dosingCells = null;
            if (windowBy.TryGetValue(key, out var w))
            {
                var marker = markerBy.TryGetValue(key, out var m) ? m.Marker : null;
                var supply = supplyBy.GetValueOrDefault(s.Cas);
                dosingCells = new DosingCells(
                    w.Floor, w.Upper, w.RecommendedPpm, marker?.CompoundMassMg ?? 0,
                    supply?.Suppliers ?? [], supply?.Risks ?? []);
            }
            else if (stoppedAt is null && dosingRan && v?.Determination == Determinations.Recommended)
            {
                // Cleared regulatory, Dosing RAN, and still no window: this substance was dropped — a missing
                // metal loading or no floor at all. The reason is already written on the DosingDoc, so it is
                // quoted rather than paraphrased.
                stoppedAt = Stages.Dosing;
                stoppedReason = (dosing?.ProvisionalReasons ?? [])
                    .FirstOrDefault(r => r.Contains(s.Cas, StringComparison.OrdinalIgnoreCase))
                    ?? "left undosed — no ppm window was produced for this substance";
            }

            OutcomeCells? outcome = null;
            if (decisionRan && confirmedBy.TryGetValue(s.ComponentId, out var confirmed))
            {
                var inCode = markerBy.TryGetValue(key, out var mm) && mm.Code.RatioSignature == confirmed
                    ? confirmed
                    : null;
                outcome = new OutcomeCells(inCode, ordered.Contains(s.Cas));

                if (stoppedAt is null && inCode is null)
                {
                    stoppedAt = Stages.Decision;
                    stoppedReason = "not selected into the final code the VP confirmed";
                }
            }

            // The one place regulatory's own absence is reported. It sits AFTER the downstream checks so a
            // row that never got a verdict cannot also be blamed on Dosing.
            if (stoppedAt is null && regulatory is null && regulatoryRan)
            {
                stoppedAt = Stages.Regulatory;
                stoppedReason = "no verdict was produced for this substance";
            }

            rows.Add(new TableRow(
                s.ComponentId, s.Cas, s.Element, s.Form,
                discovery, regulatory, dosingCells, outcome, stoppedAt, stoppedReason));
        }

        return rows;
    }
}
