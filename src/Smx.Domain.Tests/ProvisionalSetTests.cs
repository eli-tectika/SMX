using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class ProvisionalSetTests
{
    private static VerdictDoc V(string cas, string? determination = null, string? proposed = null) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "Zr", Form = "oxide",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("reg", "x", "t")], 0.9, "r")],
        Determination = determination,
        ProposedDetermination = proposed,
    };

    [Fact]
    public void Of_PrefersTheOperatorsDetermination_OverTheProposal()
    {
        // The human overrules the agent in BOTH directions, which is the whole point of a signature.
        var set = ProvisionalSet.Of([V("in", Determinations.Recommended, Determinations.Rejected)]);
        Assert.Equal("in", Assert.Single(set).Cas);

        Assert.Empty(ProvisionalSet.Of([V("out", Determinations.Rejected, Determinations.Recommended)]));
    }

    [Fact]
    public void Of_FallsBackToTheProposal_WhenNobodyHasRuled()
    {
        // This is the ONLY difference from CompliantSet, and it is why Dosing can run unattended.
        Assert.Equal("p", Assert.Single(ProvisionalSet.Of([V("p", null, Determinations.Recommended)])).Cas);
    }

    [Fact]
    public void Of_ExcludesASilentVerdict_WithNoDeterminationAndNoProposal()
    {
        Assert.Empty(ProvisionalSet.Of([V("silent")]));
    }

    [Fact]
    public void Of_FailsClosedOnANonCanonicalString()
    {
        // Same safe asymmetry as CompliantSet: a hand-edited document is not a recommendation.
        Assert.Empty(ProvisionalSet.Of([V("weird", null, "Recommended")]));
        Assert.Empty(ProvisionalSet.Of([V("weird2", "yes please")]));
    }

    [Fact]
    public void IsProvisional_IsTrue_WhenAnyMemberRestsOnAProposal()
    {
        Assert.True(ProvisionalSet.IsProvisional([V("p", null, Determinations.Recommended)]));
        Assert.False(ProvisionalSet.IsProvisional([V("s", Determinations.Recommended, null)]));
    }

    [Fact]
    public void IsProvisional_IsFalse_ForAProposedRejection()
    {
        // A proposed `rejected` puts nothing INTO the set, so it cannot make the set provisional. Reporting
        // it would attach an order-blocking flag to a substance that was never going to be dosed.
        Assert.False(ProvisionalSet.IsProvisional([V("out", null, Determinations.Rejected)]));
    }

    [Fact]
    public void ProvisionalReasons_NameEachSubstanceRestingOnAProposal()
    {
        var reasons = ProvisionalSet.ProvisionalReasons([
            V("111-11-1", null, Determinations.Recommended),
            V("222-22-2", Determinations.Recommended, null),
        ]);
        var only = Assert.Single(reasons);
        Assert.Contains("111-11-1", only);
        Assert.DoesNotContain("222-22-2", only);
    }

    [Fact]
    public void Of_IsASupersetOfCompliantSet_Always()
    {
        // The relationship that makes the two names safe to hold in one head: anything the operator signed
        // is in both, and the difference is exactly what nobody has ruled on.
        VerdictDoc[] mixed = [
            V("a", Determinations.Recommended),
            V("b", null, Determinations.Recommended),
            V("c", Determinations.Rejected),
        ];
        var compliant = CompliantSet.Of(mixed).Select(v => v.Cas).ToHashSet();
        var provisional = ProvisionalSet.Of(mixed).Select(v => v.Cas).ToHashSet();

        Assert.ProperSubset(provisional, compliant);
    }
}
