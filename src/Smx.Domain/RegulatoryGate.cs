using Smx.Domain.Records;

namespace Smx.Domain;

public static class RegulatoryGate
{
    /// The Regulatory hard gate arms only when every non-Pass verdict (the agent's flagged /
    /// low-confidence items) has been evidence-reviewed by the operator. Blockers name the
    /// cells that still need eyes.
    public static (bool Ok, IReadOnlyList<string> Blockers) Armable(IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var blockers = verdicts
            .Where(v => v.Overall != VerdictStatus.Pass && !v.EvidenceReviewed)
            .Select(v => $"unreviewed: {v.Cas}|{v.ComponentId} ({v.Overall})")
            .ToList();
        return (blockers.Count == 0, blockers);
    }
}
