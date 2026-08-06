using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// Was RegulatoryGateTests. The SUBJECT survived the gate's deletion (§16.4) — every assertion here is
/// about "which live flagged verdicts nobody has opened", which never depended on a signature existing.
/// What changed is where the answer is consulted: it is now a precondition on the VP signature and on
/// POST /orders/{cas} rather than a gate-arming predicate, and the shape is a blocker LIST (empty = clear)
/// rather than a (bool, list) pair.
public class EvidenceReviewTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall, bool reviewed) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "X", Form = "f", EvidenceReviewed = reviewed,
        // A single dimension whose status == the desired Overall (Fold = Max).
        Dimensions = [new("ElementGate", overall, [new Citation("r", "x", "t")], 0.9, "r")],
    };

    /// The LIVE analysis: which (cas, bottle) cells are actually being screened. Tier "C" is excluded from
    /// the matrix, so a C candidate has no cell — and any verdict left over for it is an orphan.
    private static CandidatesDoc C(params (string Cas, string Tier)[] substances) => new()
    {
        Id = RecordIds.Candidates("p1"), ProjectId = "p1",
        Substances = [.. substances.Select(s => new CandidateSubstance(
            "bottle", "X", "f", s.Cas, null, null, true, s.Tier, "r", [new Citation("catalog", "x", "t")]))],
    };

    private static CandidatesDoc Screened(params string[] cas) => C([.. cas.Select(c => (c, "A"))]);

    [Fact]
    public void Clear_WhenAllVerdictsCleanPass_EvenIfNotReviewed()
    {
        // A clean Pass needs no eyes. If this ever started blocking, every unattended run would be stuck
        // behind a review nobody was asked for — which is the failure mode this whole redesign exists to
        // avoid, wearing the other hat.
        Assert.Empty(EvidenceReview.Outstanding(
            Screened("a", "b"), [V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Pass, false)]));
    }

    [Fact]
    public void Blocks_WhenAFlaggedVerdictIsUnreviewed()
    {
        // THE CHECK'S REASON TO EXIST, and the proof that it can still fire now that nothing machine-side
        // sets EvidenceReviewed (REGULATORY_AUTO_APPROVE was deleted with the gate). A check that cannot
        // fail is worse than no check, because it reads as protection.
        var blockers = EvidenceReview.Outstanding(
            Screened("a", "b"), [V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Fail, false)]);
        Assert.Contains("b", Assert.Single(blockers));
    }

    [Fact]
    public void Clear_WhenEveryFlaggedVerdictIsReviewed()
    {
        Assert.Empty(EvidenceReview.Outstanding(
            Screened("a", "b"),
            [V("a", VerdictStatus.Conditional, true), V("b", VerdictStatus.NeedsReview, true)]));
    }

    [Fact]
    public void Clear_OnEmptyVerdictSet()
    {
        Assert.Empty(EvidenceReview.Outstanding(Screened(), []));
    }

    [Fact]
    public void Ignores_AnOrphanVerdict_ForACandidateARevisionRetieredToC()
    {
        // The single most natural use of revise-with-reason: "exclude Ba, it overlaps the Ti K-beta line" →
        // Discovery re-tiers Ba to C. Its pre-revision verdict (an unreviewed Fail) is still in the store,
        // but Ba is no longer screened: it appears in no matrix row, no matrix cell, and therefore in no UI
        // affordance the operator could open to review it. Blocking on it would return 422 FOREVER — a
        // permanently bricked primary journey. It is not evidence of anything; ignore it.
        Assert.Empty(EvidenceReview.Outstanding(C(("cas-ba", "C")), [V("cas-ba", VerdictStatus.Fail, false)]));
    }

    [Fact]
    public void Blocks_WhenALiveUnreviewedFlaggedVerdictSitsBesideAnOrphan()
    {
        // The narrowing above must never weaken the check itself. The orphan (Ba, re-tiered to C) drops out;
        // the LIVE unreviewed Fail (Zr, still tier A) still blocks. Anti-rubber-stamping is the whole point
        // — a flagged item on a screened cell always demands the operator's eyes.
        var blockers = EvidenceReview.Outstanding(
            C(("cas-ba", "C"), ("cas-zr", "A")),
            [V("cas-ba", VerdictStatus.Fail, false), V("cas-zr", VerdictStatus.Fail, false)]);

        Assert.Contains("cas-zr", Assert.Single(blockers));
        Assert.DoesNotContain("cas-ba", blockers[0]);
    }

    [Fact]
    public void Ignores_AVerdictForACandidateARevisionDroppedEntirely()
    {
        // A revise can also REMOVE a candidate outright rather than re-tier it. Same orphan, same reasoning.
        Assert.Empty(EvidenceReview.Outstanding(Screened("cas-zr"), [
            V("cas-zr", VerdictStatus.Pass, false),
            V("cas-gone", VerdictStatus.NeedsReview, false),
        ]));
    }
}
