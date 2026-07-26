using Smx.Domain.Records;

namespace Smx.Domain;

/// The deterministic fold (§3.5): per component, one row per RECOMMENDED substance, each row carrying
/// what it actually cleared and WHERE each claim lives (record ids). No agent input: everything here is
/// a lookup over records the operator already signed or the pipeline already computed. The agent's only
/// contribution to the decision (the final-code pick) is layered on top as a PROPOSAL by DecisionAgent.
public static class DecisionAssembler
{
    public static IReadOnlyList<ComponentDecision> Assemble(
        IReadOnlyCollection<VerdictDoc> verdicts, DosingDoc dosing, CostDoc cost,
        IReadOnlyList<string> componentIds)
    {
        var windows = dosing.Windows.ToDictionary(w => (w.ComponentId, w.Cas));
        var audits = cost.Substances.ToDictionary(a => a.Cas);

        return [.. componentIds.Select(comp => new ComponentDecision(
            comp,
            Rows:
            [
                .. verdicts
                    .Where(v => v.ComponentId == comp && v.Determination == Determinations.Recommended)
                    .OrderBy(v => v.Cas, StringComparer.Ordinal)
                    .Select(v =>
                    {
                        var window = windows.GetValueOrDefault((comp, v.Cas));
                        var audit = audits.GetValueOrDefault(v.Cas);
                        return new DecisionRow(
                            v.Cas, v.Element,
                            v.Determination!,                       // the R.E.'s word, copied verbatim
                            window?.RecommendedPpm ?? 0,            // no window ⇒ no number, never a guess
                            new ClearedCriteria(
                                Regulatory: true,                   // only recommended rows exist here
                                Dosing: window is not null,
                                Cost: audit?.BestQuote is not null),
                            new TraceRefs(
                                Verdict: RecordIds.Verdict(v.ProjectId, v.Cas, v.ComponentId),
                                Window: RecordIds.Dosing(v.ProjectId),
                                Audit: RecordIds.Cost(v.ProjectId)));
                    }),
            ],
            ProposedCode: null))];   // the agent fills this in; assembly never proposes
    }
}
