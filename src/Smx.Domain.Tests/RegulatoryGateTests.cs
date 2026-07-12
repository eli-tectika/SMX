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

    [Fact]
    public void Armable_WhenAllVerdictsCleanPass_EvenIfNotReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Pass, false)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void NotArmable_WhenAFlaggedVerdictIsUnreviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Fail, false)]);
        Assert.False(ok);
        Assert.Single(blockers);
        Assert.Contains("b", blockers[0]);
    }

    [Fact]
    public void Armable_WhenEveryFlaggedVerdictIsReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Conditional, true), V("b", VerdictStatus.NeedsReview, true)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void Armable_OnEmptyVerdictSet()
    {
        var (ok, blockers) = RegulatoryGate.Armable([]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }
}
