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
        else if (decision.Components.Count == 0)
            // Unreachable today via upstream guarantees (DecisionAssembler emits one ComponentDecision per
            // constraints component), but Armable is a STANDALONE predicate — and the signing endpoint's
            // confirm loop iterates decision.Components, so an armable zero-component decision would let an
            // approval vacuously "confirm" nothing. An empty decision is not a decision.
            blockers.Add("decision covers no components");
        else
            blockers.AddRange(decision.Components
                .Where(c => c.ProposedCode is null)
                .Select(c => $"component '{c.ComponentId}' has no proposed code"));

        return (blockers.Count == 0, blockers);
    }

    /// "A signature answers a park" (Task 15(d)): a VP determination is only meaningful while the Decision
    /// stage is parked `awaiting-VP`. Null means signable; anything else returns the blocker naming the
    /// actual status. The one helper serves three surfaces — POST …/decision/determination (the 422),
    /// GET …/gate/vp and the dashboard's vp card — so the reads can never advertise a gate the POST
    /// refuses. The two states it exists to refuse: `pending` mid-re-pick (a Dosing revision reset the
    /// stage while the STALE DecisionDoc is still on file — signing it would let the in-flight re-pick
    /// overwrite a stamped doc under an approved gate) and `done` post-close (approve or REJECT would
    /// rewrite history — a gate flipped locked over Released procurement revokes nothing).
    public static string? ParkBlocker(string? decisionStageStatus) =>
        decisionStageStatus == "awaiting-VP"
            ? null
            : $"the decision stage is '{decisionStageStatus ?? "absent"}', not 'awaiting-VP' — " +
              "a signature answers a park, never a draft, never history";

    /// The revision-in-flight blocker (Task 15 review F1, layer 3). The revise run is minutes wide (two
    /// LLM calls, an embed, a search push) and the stage still advertises `awaiting-VP` throughout, so
    /// ParkBlocker cannot see it — but the RevisionDoc CAN be seen: it is durable from POST /revise's 202
    /// until the executor marks it applied/failed, covering the whole window including feed lag. A pending
    /// Dosing or Decision revision means the decision may be about to change; nothing may be signed over
    /// words that are being rewritten. Same three surfaces as ParkBlocker: the POST's 422, GET /gate/vp,
    /// and the dashboard's vp card.
    ///
    /// Upstream stages (Discovery/Regulatory) are deliberately not listed: their revisions void the
    /// REGULATORY gate when they land, and Armable + the coverage re-check already refuse an unsigned or
    /// uncovered analysis — that window has its own guards.
    public static string? PendingRevisionBlocker(IReadOnlyList<RevisionDoc> revisions)
    {
        var pending = revisions.FirstOrDefault(r =>
            r.Status == RevisionStatus.Pending && r.Stage is Stages.Dosing or Stages.Decision);
        return pending is null
            ? null
            : $"a revision of {pending.Stage} is pending — the decision may be about to change; " +
              "sign after it lands";
    }
}
