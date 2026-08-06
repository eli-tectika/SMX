using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// WHICH CHEMICALS MAY REACH A CUSTOMER'S PRODUCT. Still the one set the irreversible acts consult, and
/// still the file to open when someone proposes widening it.
///
/// The rule CHANGED with the 2026-08-06 redesign §16.4, and the change reads as a loosening, so the reason
/// belongs here rather than only in the source. This used to admit ONLY substances an operator had marked
/// `recommended`. The writer of those determinations was the regulatory hard gate — and §16.4 deleted that
/// gate. Left strict, this set would have been empty on every project forever: dosing over nothing, orders
/// refused permanently, and no error anywhere. An app that quietly never lets anyone buy anything is a
/// worse failure than the one strictness was buying, because nobody would ever see it.
///
/// So the agent's proposal became the default admission and the operator's ruling became an OVERRIDE. The
/// tests that matter most in this file are therefore the two VETO tests: a `rejected` is absolute, and
/// nothing — not a hopeful proposal, not a later run — puts a vetoed substance back.
public class CompliantSetTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall,
        string? determination = null, string? proposed = null) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "Zr", Form = "oxide",
        Dimensions = [new("ElementGate", overall, [new Citation("reg", "x", "t")], 0.9, "r")],
        Determination = determination,
        ProposedDetermination = proposed,
    };

    [Fact]
    public void Of_IncludesWhatTheOperatorRecommended()
    {
        var set = CompliantSet.Of([
            V("cas-in",  VerdictStatus.Pass, Determinations.Recommended),
            V("cas-out", VerdictStatus.Pass, Determinations.Rejected),
        ]);
        Assert.Equal("cas-in", Assert.Single(set).Cas);
    }

    [Fact]
    public void Of_ADMITS_TheAgentsProposal_WhenNobodyHasRuled()
    {
        // Was Of_IGNORES_TheAgentsProposal_EntirelyAndOnPurpose, and its inversion is the whole of §16.4.
        // With the regulatory gate deleted there is no writer of operator determinations at all, so a set
        // that ignored proposals would be empty on every project — dosing nothing, ordering nothing, and
        // looking perfectly healthy while doing it.
        //
        // What replaces the strictness is not nothing: the operator's veto below, EvidenceReview.Outstanding
        // refusing the VP pen and the order while a flagged item is unopened, and the matrix rendering
        // proposal and determination as separate columns so an unruled substance never looks ruled.
        var proposed = V("cas-1", VerdictStatus.Pass, proposed: Determinations.Recommended);
        proposed.ProposedReason = "the agent is very confident";

        Assert.Equal("cas-1", Assert.Single(CompliantSet.Of([proposed])).Cas);
    }

    [Fact]
    public void Of_EXCLUDES_AnOperatorVeto_EvenOverAnAgentRecommendation()
    {
        // THE LINE THAT REPLACED THE OLD ONE. The proposal is the default, but a human's `rejected` is
        // absolute and unrescuable: this is the direction in which a mistake ships a chemical the operator
        // explicitly refused into a customer's product. This test failing is a design alarm.
        Assert.Empty(CompliantSet.Of([
            V("cas-1", VerdictStatus.Pass, Determinations.Rejected, Determinations.Recommended),
        ]));
    }

    [Fact]
    public void Of_EXCLUDES_ASilentVerdict_NeitherRuledNorProposed()
    {
        // The failed-verdict fallback leaves both fields null (an un-screenable substance). Silence is not
        // consent, and that half of the old rule is untouched.
        Assert.Empty(CompliantSet.Of([V("cas-1", VerdictStatus.NeedsReview)]));
    }

    [Fact]
    public void Of_EXCLUDES_AProposedRejection()
    {
        Assert.Empty(CompliantSet.Of([V("cas-1", VerdictStatus.Fail, proposed: Determinations.Rejected)]));
    }

    [Fact]
    public void Of_HonoursAnOperatorOverrideOfAFail_BecauseThatIsWhatAnOverrideIsFor()
    {
        // The override runs both ways: the operator may overrule the agent's Fail, and it carries a
        // mandatory reason. Their ruling is the authority wherever they gave one.
        var overridden = V("cas-1", VerdictStatus.Fail, Determinations.Recommended, Determinations.Rejected);
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
            V("cas-4", VerdictStatus.Pass, proposed: "Recommended"),
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
