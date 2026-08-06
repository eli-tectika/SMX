using Smx.Domain.Records;

namespace Smx.Domain;

/// THE ANTI-RUBBER-STAMPING RULE, OUTLIVING THE GATE IT WAS BORN IN.
///
/// This was `RegulatoryGate.Armable` — the predicate that decided whether the regulatory hard gate could be
/// signed. The 2026-08-06 redesign §16.4 deleted that gate, and the obvious move would have been to delete
/// this with it. It was kept, renamed, and re-pointed, because what it asserts never actually depended on a
/// signature existing:
///
///     no live non-Pass verdict may still be UNOPENED when something irreversible happens.
///
/// A flagged or low-confidence screening result that nobody has looked at is exactly the thing that must not
/// travel silently into a compliance package or a purchase order. The gate is gone; the flagged verdict is
/// still flagged. So this is now a PRECONDITION ON THE IRREVERSIBLE ACTS (POST /decision/determination,
/// POST /orders/{cas}, and the two reads that must not advertise what those POSTs would refuse) rather than
/// a gate-arming predicate.
///
/// IT CAN FAIL, WHICH IS THE ONLY REASON IT IS WORTH KEEPING. `EvidenceReviewed` has exactly two writers,
/// both operator endpoints — POST /regulatory/review (opening an item) and POST /regulatory/determination
/// (ruling on one). The machine has no way to set it since REGULATORY_AUTO_APPROVE was deleted alongside the
/// gate, so an untouched project with a flagged verdict genuinely blocks here, and the operator clears it by
/// opening the item. A check nobody can trip is worse than no check, because it reads as protection.
///
/// ONLY THE LIVE CELLS COUNT — exactly the set MatrixAssembler is screening. A revise-with-reason can re-tier
/// a candidate to C, or drop it from the set entirely, and leave its old verdict behind describing a cell
/// nobody screens any more: it appears in no matrix and therefore in no UI affordance, so blocking on it
/// would deadlock the operator on an item they cannot even open to clear — a permanent 422 on the primary
/// journey. Judge the CURRENT analysis, not everything ever written.
///
/// This narrows what BLOCKS, never what is REVIEWED: a live unreviewed non-pass verdict still blocks.
public static class EvidenceReview
{
    /// The live non-Pass verdicts nobody has opened, named one per line. EMPTY means clear — callers read
    /// `is { Count: > 0 }`, so there is no bool to get the wrong way round beside a list to forget.
    public static IReadOnlyList<string> Outstanding(
        CandidatesDoc candidates, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var live = MatrixAssembler.Cells(candidates).ToHashSet();
        return
        [
            .. verdicts
                .Where(v => live.Contains((v.Cas, v.ComponentId)))
                .Where(v => v.Overall != VerdictStatus.Pass && !v.EvidenceReviewed)
                .Select(v => $"unreviewed: {v.Cas}|{v.ComponentId} ({v.Overall})"),
        ];
    }
}
