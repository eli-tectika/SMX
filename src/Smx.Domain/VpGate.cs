using Smx.Domain.Records;

namespace Smx.Domain;

public static class VpGate
{
    /// The VP hard gate arms only when the regulatory gate is APPROVED and the Decision stage has produced
    /// a code offer for EVERY component (spec §4 gates table: "Regulatory cleared + all components have a
    /// selected code"). "Selected" at ARM time means a ProposedCode is present — the agent's offer for the
    /// VP to confirm or override IN the signing call; the confirmation itself never gates arming, or the
    /// gate could only be signed after it was signed.
    ///
    /// The regulatory signature is checked first because the decision matrix is assembled FROM the
    /// compliant set that signature vouches for: a VP signature over an unsigned compliance analysis would
    /// stack one gate on a void. Blockers name things precisely — the missing signature, the absent
    /// decision, or the exact component with no code — because a blocker the operator cannot locate is a
    /// gate they cannot ever arm.
    public static (bool Ok, IReadOnlyList<string> Blockers) Armable(
        GateDoc? regulatoryGate, DecisionDoc? decision)
    {
        var blockers = new List<string>();

        if (regulatoryGate?.Status != "approved")
            blockers.Add("regulatory gate is not approved");

        if (decision is null)
            blockers.Add("decision has not run");
        else
            blockers.AddRange(decision.Components
                .Where(c => c.ProposedCode is null)
                .Select(c => $"component '{c.ComponentId}' has no proposed code"));

        return (blockers.Count == 0, blockers);
    }
}
