using Smx.Domain.Records;

namespace Smx.Domain;

/// Which substances Dosing may dose — i.e. which chemicals may reach a customer's product. Still the ONE
/// set the irreversible acts consult (the compliance-package export, placing an order).
///
/// THE RULE: everything the agent did not reject, MINUS anything the operator vetoed.
///
///   include  ⟺  Determination == recommended
///             OR (Determination is null AND ProposedDetermination == recommended)
///   exclude  ⟺  Determination == rejected            ← an operator veto always wins
///
/// WHY IT IS NOT STRICTER, WHICH IS THE PART THAT NEEDS EXPLAINING. It used to read ONLY the operator's
/// `Determination`, on the reasoning that nothing reaches a customer's product without a named human saying
/// yes. The writer of those determinations was the regulatory hard gate — and the 2026-08-06 redesign §16.4
/// DELETED that gate outright (the matrix carries confidence and sources, and that is the review surface).
///
/// Left as it was, this set would be EMPTY on every project forever: dosing would run over nothing,
/// `POST /orders/{cas}` would refuse permanently, and the app would look completely healthy while quietly
/// never letting anyone buy anything. That is a worse failure than the one strictness was buying, because
/// it is silent. So the default admission moved to the agent's proposal, and the operator's ruling became
/// an OVERRIDE — still recorded, still carrying a mandatory reason (POST /regulatory/determination).
///
/// WHAT IS PRESERVED. The veto is absolute and reads FIRST: a signed `rejected` is never resurrected by a
/// hopeful proposal, in either direction. A substance the agent proposed OUT (or could not screen at all —
/// the failed-verdict fallback leaves both fields null) is still excluded. The comparison is ordinal: a
/// non-canonical string from a hand-edited document is not a recommendation and is DROPPED, the safe
/// asymmetry — an omission, never a false pass.
///
/// `EvidenceReviewed` is NOT re-checked here. The anti-rubber-stamping rule outlived the gate as
/// <see cref="EvidenceReview"/>, which the two irreversible acts consult directly; a copy of it here could
/// only drift out of step with it.
public static class CompliantSet
{
    public static IReadOnlyList<VerdictDoc> Of(IReadOnlyList<VerdictDoc> verdicts) =>
        [.. verdicts.Where(v => Effective(v) == Determinations.Recommended)];

    /// The operator's determination WINS wherever it exists — that is the whole of the override rule, and
    /// writing it as a null-coalesce rather than two branches is what makes a `rejected` unrescuable.
    private static string? Effective(VerdictDoc v) => v.Determination ?? v.ProposedDetermination;
}
