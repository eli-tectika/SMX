using Smx.Domain.Records;

namespace Smx.Domain;

/// Which substances Dosing may COMPUTE over — deliberately NOT the same question as which substances may
/// reach a customer's product. That second question is <see cref="CompliantSet"/>'s, and it is stricter.
///
/// The split exists because the pipeline no longer parks (execution-core design §8/D10, completed by the
/// 2026-08-06 redesign spec §10.1). Regulatory lands with its verdicts carrying only the agent's PROPOSED
/// determinations; if Dosing ran over <see cref="CompliantSet"/> at that moment it would run over an EMPTY
/// set and produce nothing, while LOOKING as though it had finished. That is strictly worse than parking —
/// a park at least says what it is waiting for.
///
/// So this set folds `Determination ?? ProposedDetermination`, and everything downstream that could act
/// irreversibly — the compliance-package export, placing an order — keeps consulting CompliantSet.
///
/// THE LINE: the machine may COMPUTE over its own proposals; it may not ACT on them. Do not "simplify"
/// these two into one set. Making CompliantSet fall back to a proposal is the agent signing the regulatory
/// gate by the back door, and <c>CompliantSetTests</c> calls that out by name as a design alarm.
///
/// The operator's determination always WINS where it exists, in BOTH directions — a signed `rejected` is
/// not resurrected by a hopeful proposal.
public static class ProvisionalSet
{
    public static IReadOnlyList<VerdictDoc> Of(IReadOnlyList<VerdictDoc> verdicts) =>
        [.. verdicts.Where(v => Effective(v) == Determinations.Recommended)];

    /// True when ANY member of the resulting set rests on a proposal rather than a signature. A DosingDoc
    /// computed while this is true is stamped provisional and cannot release an order.
    public static bool IsProvisional(IReadOnlyList<VerdictDoc> verdicts) =>
        verdicts.Any(RestsOnAProposal);

    /// One human-readable line per substance that is in the set ONLY because the agent proposed it. Named
    /// substances, never a count: "3 substances are provisional" is not something an operator can act on.
    public static IReadOnlyList<string> ProvisionalReasons(IReadOnlyList<VerdictDoc> verdicts) =>
    [
        .. verdicts.Where(RestsOnAProposal).Select(v =>
            $"{v.Element} ({v.Cas}) in '{v.ComponentId}' is included on the agent's proposal alone — " +
            $"no operator determination is on file."),
    ];

    /// A substance is only provisional if the agent proposed it IN and nobody has ruled. A proposed
    /// `rejected` contributes nothing to the set, so it cannot make the set provisional either.
    private static bool RestsOnAProposal(VerdictDoc v) =>
        v.Determination is null && v.ProposedDetermination == Determinations.Recommended;

    /// Ordinal comparison downstream, exactly like CompliantSet's: a non-canonical string (a hand-edited
    /// document) is not a recommendation, and is dropped. The safe asymmetry — an omission, never a false
    /// pass.
    private static string? Effective(VerdictDoc v) => v.Determination ?? v.ProposedDetermination;
}
