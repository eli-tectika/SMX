using Smx.Domain.Records;

namespace Smx.Domain;

public static class RegulatoryGate
{
    /// The Regulatory hard gate arms only when every non-Pass verdict (the agent's flagged /
    /// low-confidence items) has been evidence-reviewed by the operator. Blockers name the
    /// cells that still need eyes.
    ///
    /// Only the LIVE cells count — exactly the set MatrixAssembler is screening. A revise-with-reason can
    /// re-tier a candidate to C, or drop it from the set entirely, and leave its old verdict behind. That
    /// verdict describes a cell nobody is screening any more: it appears in no matrix and therefore in no
    /// UI affordance, so blocking the gate on it would deadlock the operator on an item they cannot even
    /// open to clear — a permanent 422 on the primary journey. Arm on the CURRENT analysis, not on
    /// everything that was ever written.
    ///
    /// This narrows what BLOCKS, never what is REVIEWED: a live unreviewed non-pass verdict still blocks,
    /// which is the whole anti-rubber-stamping point of the gate.
    public static (bool Ok, IReadOnlyList<string> Blockers) Armable(
        CandidatesDoc candidates, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var live = MatrixAssembler.Cells(candidates).ToHashSet();
        var blockers = verdicts
            .Where(v => live.Contains((v.Cas, v.ComponentId)))
            .Where(v => v.Overall != VerdictStatus.Pass && !v.EvidenceReviewed)
            .Select(v => $"unreviewed: {v.Cas}|{v.ComponentId} ({v.Overall})")
            .ToList();
        return (blockers.Count == 0, blockers);
    }
}
