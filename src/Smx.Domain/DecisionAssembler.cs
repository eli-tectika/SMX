using Smx.Domain.Records;

namespace Smx.Domain;

/// The deterministic fold (§3.5): per component, one row per RECOMMENDED substance, each row carrying
/// what it actually cleared and WHERE each claim lives (record ids). No agent input: everything here is
/// a lookup over records the operator already signed or the pipeline already computed. The agent's only
/// contribution to the decision (the final-code pick) is layered on top as a PROPOSAL by DecisionAgent.
public static class DecisionAssembler
{
    public static IReadOnlyList<ComponentDecision> Assemble(
        IReadOnlyCollection<VerdictDoc> verdicts, DosingDoc dosing,
        IReadOnlyList<string> componentIds)
    {
        var windows = dosing.Windows.ToDictionary(w => (w.ComponentId, w.Cas));
        // The supply audit now rides on the dosing doc -- the Cost stage that used to carry it is deleted
        // (redesign spec §6). GroupBy+First rather than ToDictionary: Supply is agent-adjacent data on a
        // persisted document, and a duplicated CAS there would throw where the old separate doc could not.
        var audits = dosing.Supply.GroupBy(a => a.Cas).ToDictionary(g => g.Key, g => g.First());

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
                                // AVAILABILITY, not cost: there are no prices, so what this criterion can
                                // honestly assert is that somebody sells the substance. Renamed rather than
                                // dropped -- shrinking what the VP signs over should be a decision.
                                Availability: audit is { Suppliers.Count: > 0 }),
                            // Two refs, not three. `Audit` pointed at the cost document; with supply folded
                            // into dosing it would be the same id as `Window` on every row -- a trace that
                            // traces to itself.
                            new TraceRefs(
                                Verdict: RecordIds.Verdict(v.ProjectId, v.Cas, v.ComponentId),
                                Window: RecordIds.Dosing(v.ProjectId)));
                    }),
            ],
            ProposedCode: null))];   // the agent fills this in; assembly never proposes
    }
}
