using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class CompliantSetTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall, string? determination = null) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "Zr", Form = "oxide",
        Dimensions = [new("ElementGate", overall, [new Citation("reg", "x", "t")], 0.9, "r")],
        Determination = determination,
    };

    [Fact]
    public void Of_IncludesOnlyWhatTheOPERATORRecommended()
    {
        var set = CompliantSet.Of([
            V("cas-in",  VerdictStatus.Pass, Determinations.Recommended),
            V("cas-out", VerdictStatus.Pass, Determinations.Rejected),
        ]);
        Assert.Equal("cas-in", Assert.Single(set).Cas);
    }

    [Fact]
    public void Of_EXCLUDES_ACleanPassTheOperatorNeverDetermined()
    {
        // The strict rule: nothing reaches a customer's product without a named human saying yes to it. The
        // Regulatory AGENT pre-fills a PROPOSAL so this is a confirmation, not an authoring burden — but a
        // proposal is not a determination.
        Assert.Empty(CompliantSet.Of([V("cas-1", VerdictStatus.Pass)]));
    }

    [Fact]
    public void Of_IGNORES_TheAgentsProposal_EntirelyAndOnPurpose()
    {
        // THE LAW-9 LINE, AT THE DOSING BOUNDARY. If a proposal could carry a substance into the compliant
        // set, the agent would be signing the regulatory gate through the back door. The two fields are
        // different fields, and only the operator's counts. This test failing is a design alarm.
        var proposed = V("cas-1", VerdictStatus.Pass);
        proposed.ProposedDetermination = Determinations.Recommended;
        proposed.ProposedReason = "the agent is very confident";

        Assert.Empty(CompliantSet.Of([proposed]));
    }

    [Fact]
    public void Of_HonoursAnOperatorOverrideOfAFail_BecauseThatIsWhatAHumanGateIsFor()
    {
        // The R.E. may overrule the agent's Fail — that is the point of a human gate, and the override
        // carries a mandatory reason. The signature is the authority.
        var overridden = V("cas-1", VerdictStatus.Fail, Determinations.Recommended);
        overridden.DeterminationReason = "the listing was superseded in the March amendment";
        Assert.Single(CompliantSet.Of([overridden]));
    }

    [Fact]
    public void Of_OnANonCanonicalDeterminationString_FailsCLOSED()
    {
        // The comparison is ordinal and case-sensitive, and that asymmetry is the safe one. Nothing but the
        // determination endpoint writes this field, and it 422s anything that is not exactly one of the two
        // constants — but if a hand-edited document ever carried "Recommended" or " recommended ", the cost
        // is an infuriating omission, never a substance dosed on a ruling nobody made.
        Assert.Empty(CompliantSet.Of([
            V("cas-1", VerdictStatus.Pass, "Recommended"),
            V("cas-2", VerdictStatus.Pass, " recommended "),
            V("cas-3", VerdictStatus.Pass, "approved"),
        ]));
    }

    [Fact]
    public void Of_OnAColdProject_ReturnsEmpty_RatherThanThrowing()
    {
        // Dosing calls this on whatever GetVerdictsAsync returned, and that is an empty list on a project
        // where Regulatory has not run. The cold path must be a no-op, not an exception.
        Assert.Empty(CompliantSet.Of([]));
    }
}
