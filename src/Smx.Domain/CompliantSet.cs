using Smx.Domain.Records;

namespace Smx.Domain;

/// Which substances Dosing may dose — i.e. which chemicals may reach a customer's product.
///
/// The rule is strict and it is deliberately strict: ONLY what the OPERATOR recommended. Not what the agent
/// proposed (that is a separate field, and reading it here would let the agent sign the regulatory gate by
/// the back door). Not a clean Pass nobody spoke about (silence is not consent). An operator override of a
/// Fail IS honoured — that is what a human gate is for, and it carries a mandatory reason.
///
/// The Regulatory agent pre-fills ProposedDetermination precisely so this strictness costs the operator a
/// confirmation rather than an authoring burden.
///
/// The comparison is ordinal: a non-canonical string (a hand-edited document) is not a recommendation and
/// is dropped. That is the safe asymmetry — an omission, never a false pass. EvidenceReviewed is NOT
/// re-checked here; the anti-rubber-stamping rule lives in RegulatoryGate.Armable, which will not let the
/// gate arm — and so will not let Dosing run — while a live flagged item is still unopened. One rule, one
/// place: a copy here could only drift out of step with it.
public static class CompliantSet
{
    public static IReadOnlyList<VerdictDoc> Of(IReadOnlyList<VerdictDoc> verdicts) =>
        verdicts.Where(v => v.Determination == Determinations.Recommended).ToList();
}
