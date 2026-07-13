using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RegulatoryGateTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall, bool reviewed)
    {
        var status = overall; // single dimension whose status == desired Overall (Fold = Max)
        return new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
            Element = "X", Form = "f", EvidenceReviewed = reviewed,
            Dimensions = [new("ElementGate", status, [new Citation("r", "x", "t")], 0.9, "r")],
        };
    }

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
    public void Armable_WhenAllVerdictsCleanPass_EvenIfNotReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable(
            Screened("a", "b"), [V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Pass, false)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void NotArmable_WhenAFlaggedVerdictIsUnreviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable(
            Screened("a", "b"), [V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Fail, false)]);
        Assert.False(ok);
        Assert.Single(blockers);
        Assert.Contains("b", blockers[0]);
    }

    [Fact]
    public void Armable_WhenEveryFlaggedVerdictIsReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable(
            Screened("a", "b"), [V("a", VerdictStatus.Conditional, true), V("b", VerdictStatus.NeedsReview, true)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void Armable_OnEmptyVerdictSet()
    {
        var (ok, blockers) = RegulatoryGate.Armable(Screened(), []);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void Armable_IgnoresAnOrphanVerdict_ForACandidateARevisionRetieredToC()
    {
        // The single most natural use of revise-with-reason: "exclude Ba, it overlaps the Ti K-beta line" →
        // Discovery re-tiers Ba to C. Its pre-revision verdict (an unreviewed Fail) is still in the store,
        // but Ba is no longer screened: it appears in no matrix row, no matrix cell, and therefore in no UI
        // affordance the operator could open to review it. Blocking the gate on it would return 422 FOREVER
        // — a permanently bricked primary journey. It is not evidence of anything; ignore it.
        var (ok, blockers) = RegulatoryGate.Armable(
            C(("cas-ba", "C")), [V("cas-ba", VerdictStatus.Fail, false)]);

        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void NotArmable_WhenALiveUnreviewedFlaggedVerdictSitsBesideAnOrphan()
    {
        // The narrowing above must never weaken the gate itself. The orphan (Ba, re-tiered to C) drops out;
        // the LIVE unreviewed Fail (Zr, still tier A) still blocks. Anti-rubber-stamping is the whole point
        // of this gate — a flagged item on a screened cell always demands the operator's eyes.
        var (ok, blockers) = RegulatoryGate.Armable(
            C(("cas-ba", "C"), ("cas-zr", "A")),
            [V("cas-ba", VerdictStatus.Fail, false), V("cas-zr", VerdictStatus.Fail, false)]);

        Assert.False(ok);
        Assert.Single(blockers);
        Assert.Contains("cas-zr", blockers[0]);
        Assert.DoesNotContain("cas-ba", blockers[0]);
    }

    [Fact]
    public void Armable_IgnoresAVerdictForACandidateARevisionDroppedEntirely()
    {
        // A revise can also REMOVE a candidate outright rather than re-tier it. Same orphan, same reasoning.
        var (ok, blockers) = RegulatoryGate.Armable(Screened("cas-zr"), [
            V("cas-zr", VerdictStatus.Pass, false),
            V("cas-gone", VerdictStatus.NeedsReview, false),
        ]);

        Assert.True(ok);
        Assert.Empty(blockers);
    }
}
