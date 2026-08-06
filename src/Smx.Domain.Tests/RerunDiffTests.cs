using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// The §9.5 rerun diff. Every assertion here stands for a way an operator could be told the wrong thing about
/// a record they have already read and reasoned about — which is the specific harm the diff exists to prevent.
public class RerunDiffTests
{
    private static RerunDiff.VerdictSnapshot V(
        string cas, string component, string element, VerdictStatus overall, string? determination = null) =>
        new(cas, component, element, overall, determination);

    // ---------------------------------------------------------------------------------------------
    // Rule 2: absence must never read as "no change".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OfVerdicts_WithNoPriorState_SaysSo_RatherThanReturningAnEmptyDiff()
    {
        // An empty diff renders as "nothing changed". If the caller could not compare, that is a LIE with the
        // operator's confidence behind it: they would read "nothing changed" over a record that may have been
        // rewritten wholesale. `Comparable` and a non-empty Summary are what keep the two apart.
        var diff = RerunDiff.OfVerdicts(null, [V("cas-eu", "bottle", "Eu", VerdictStatus.Pass)]);

        Assert.False(diff.Comparable);
        Assert.Contains("No prior state to compare", diff.Summary);
        Assert.NotEmpty(diff.Summary);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void OfVerdicts_TreatsAnEmptyBeforeAsNoPriorState_NotAsEveryVerdictAdded()
    {
        // A project's FIRST regulatory screen must not read as "14 verdicts added". "Added" means a change to
        // something the operator has previously seen; presenting an initial analysis that way would put a
        // change notice on a record nobody had read yet, and teach them to ignore the next one.
        var diff = RerunDiff.OfVerdicts([], [V("cas-eu", "bottle", "Eu", VerdictStatus.Pass)]);

        Assert.False(diff.Comparable);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void OfVerdicts_UnchangedAndNotComparable_AreDistinguishable()
    {
        // Both produce an empty Lines list, and they mean opposite things: "your sign-off still describes the
        // record" versus "nothing can be said about the record". A renderer keying on Lines alone would blur
        // them; Summary and Comparable are the fields that cannot.
        var one = V("cas-eu", "bottle", "Eu", VerdictStatus.Pass);
        var unchanged = RerunDiff.OfVerdicts([one], [one]);
        var incomparable = RerunDiff.OfVerdicts(null, [one]);

        Assert.Empty(unchanged.Lines);
        Assert.Empty(incomparable.Lines);
        Assert.NotEqual(unchanged.Summary, incomparable.Summary);
        Assert.True(unchanged.Comparable);
        Assert.False(incomparable.Comparable);
        Assert.Contains("no verdict changed", unchanged.Summary);
    }

    [Fact]
    public void AllLines_AlwaysCarriesTheSummary_SoTheHeadlineCannotBeDropped()
    {
        // The convenience the callers emit. If it returned only the detail lines, the unchanged case and the
        // incomparable case would both emit NOTHING — rule 2 defeated at the wiring instead of in the domain.
        Assert.Single(RerunDiff.OfVerdicts(null, []).AllLines);
        Assert.Single(RerunDiff.OfVerdicts(
            [V("cas-eu", "bottle", "Eu", VerdictStatus.Pass)],
            [V("cas-eu", "bottle", "Eu", VerdictStatus.Pass)]).AllLines);
    }

    // ---------------------------------------------------------------------------------------------
    // Rule 3: name the substance, never a bare count.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AChangedStatus_NamesTheSubstanceTheComponentAndBothEnds()
    {
        // The spec's own example. "3 verdicts changed" is not actionable — the operator cannot tell which
        // three, in which components, or in which direction, and a rerun they cannot act on is a rerun they
        // will learn to scroll past.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass)],
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Fail)]);

        Assert.True(diff.Comparable);
        Assert.Equal("Eu (14589-40-3) in 'bottle': Pass -> Fail.", Assert.Single(diff.Lines));
        var change = Assert.Single(diff.Changes);
        Assert.Equal(RerunDiff.Kinds.StatusChanged, change.Kind);
        Assert.Equal("Pass", change.Before);
        Assert.Equal("Fail", change.After);
    }

    [Fact]
    public void AClearedDetermination_IsReportedAsNotRuled_NotAsBlank()
    {
        // A rerun REPLACES the VerdictDoc, and the agent's fresh one carries Determination = null — so a
        // rerun un-signs the operator's own ruling. That has to be said out loud here rather than discovered
        // three screens away when the regulatory gate silently refuses to arm. Rendering null as "" would
        // produce "determination  -> not ruled", which reads as a formatting bug, not as a lost signature.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass, Determinations.Recommended)],
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass)]);

        Assert.Equal("Eu (14589-40-3) in 'bottle': determination recommended -> not ruled.",
            Assert.Single(diff.Lines));
        Assert.Equal(RerunDiff.Kinds.DeterminationChanged, Assert.Single(diff.Changes).Kind);
    }

    [Fact]
    public void AStatusAndADeterminationCanBothMove_AndBothAreReported()
    {
        // One substance, two independent facts. Reporting only the first would let the operator believe the
        // ruling still stands over a verdict that flipped to Fail.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass, Determinations.Recommended)],
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Fail, Determinations.Rejected)]);

        Assert.Equal(2, diff.Changes.Count);
        Assert.Contains(diff.Changes, c => c.Kind == RerunDiff.Kinds.StatusChanged);
        Assert.Contains(diff.Changes, c => c.Kind == RerunDiff.Kinds.DeterminationChanged);
        Assert.Contains("2 verdicts changed", diff.Summary);
    }

    [Fact]
    public void ARemovedVerdict_ReadsAsAHoleInTheAnalysis_NotAsATidyUp()
    {
        // A substance that WAS screened and now is not leaves the record with no verdict for a candidate that
        // may still be dosed. The quiet phrasing ("1 verdict removed") is the dangerous one; this line has to
        // survive being skim-read.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass)], []);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(RerunDiff.Kinds.Removed, change.Kind);
        Assert.Contains("NO LONGER SCREENED", change.Line);
        Assert.Contains("Eu (14589-40-3) in 'bottle'", change.Line);
    }

    [Fact]
    public void ANewlyScreenedVerdict_IsReportedWithTheStatusItLandedOn()
    {
        // An amendment that adds a component adds verdicts. Announcing the addition without the status would
        // make a brand-new Fail look like routine bookkeeping.
        var diff = RerunDiff.OfVerdicts(
            [V("cas-zr", "bottle", "Zr", VerdictStatus.Pass)],
            [V("cas-zr", "bottle", "Zr", VerdictStatus.Pass), V("14589-40-3", "lid", "Eu", VerdictStatus.Fail)]);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(RerunDiff.Kinds.Added, change.Kind);
        Assert.Equal("Eu (14589-40-3) in 'lid': newly screened — Fail.", change.Line);
    }

    // ---------------------------------------------------------------------------------------------
    // The key: (Cas, ComponentId), never Cas alone.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OneSubstanceInTwoComponents_IsTwoVerdicts_NotOne()
    {
        // Per-component tracks (interaction law 1): there is no product-wide marker, and the same CAS can pass
        // in the bottle and fail on the lid. Keying on CAS alone would collapse the two — one row would
        // overwrite the other, and the diff would report a change to the wrong component or miss it entirely.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass), V("14589-40-3", "lid", "Eu", VerdictStatus.Pass)],
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass), V("14589-40-3", "lid", "Eu", VerdictStatus.Fail)]);

        var change = Assert.Single(diff.Changes);
        Assert.Contains("in 'lid'", change.Line);
        Assert.DoesNotContain("in 'bottle'", change.Line);
    }

    [Fact]
    public void TheKeyIsOrdinal_SoADifferentlyCasedComponentIsADifferentTrack()
    {
        // Ordinal on both members, matching PipelineRunner.RunRegulatoryAsync's own `existing` set. If the two
        // disagreed, the runner would consider a verdict present while the diff called it added — a change
        // notice for a screen that never ran.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass)],
            [V("14589-40-3", "Bottle", "Eu", VerdictStatus.Pass)]);

        Assert.Equal(2, diff.Changes.Count); // one removed, one added — NOT silently paired
    }

    [Fact]
    public void TheOutputIsDeterministic_RegardlessOfInputOrder()
    {
        // The store's read order is not guaranteed, and a diff that reorders itself run to run is one nobody
        // can eyeball for "did anything move since last time".
        RerunDiff.VerdictSnapshot[] before =
        [
            V("cas-zr", "bottle", "Zr", VerdictStatus.Pass),
            V("14589-40-3", "lid", "Eu", VerdictStatus.Pass),
            V("cas-y", "bottle", "Y", VerdictStatus.Pass),
        ];
        RerunDiff.VerdictSnapshot[] after =
        [
            V("cas-y", "bottle", "Y", VerdictStatus.Fail),
            V("cas-zr", "bottle", "Zr", VerdictStatus.Fail),
            V("14589-40-3", "lid", "Eu", VerdictStatus.Fail),
        ];

        Assert.Equal(
            RerunDiff.OfVerdicts(before, after).Lines,
            RerunDiff.OfVerdicts([.. before.Reverse()], [.. after.Reverse()]).Lines);
    }

    [Fact]
    public void ANonCanonicalDetermination_IsQuoted_SoItCannotPassForALegalRuling()
    {
        // Only two strings are legal determinations (Determinations.Recommended / .Rejected) and the endpoint
        // 422s anything else — so a third value in the record is a hand-edited document. Printing it bare
        // would let "Recommended" (wrong case, and therefore ignored by CompliantSet) read on this line
        // exactly like the ruling that actually lets a chemical into a product.
        var diff = RerunDiff.OfVerdicts(
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass)],
            [V("14589-40-3", "bottle", "Eu", VerdictStatus.Pass, "Recommended")]);

        Assert.Contains("-> 'Recommended'.", Assert.Single(diff.Lines));
    }

    // ---------------------------------------------------------------------------------------------
    // Dosing.
    // ---------------------------------------------------------------------------------------------

    private static DosingDoc Dosing(
        IEnumerable<PpmWindow>? windows = null, IEnumerable<MarkerCode>? codes = null, bool provisional = false) =>
        new()
        {
            Id = "dosing|p1", ProjectId = "p1", GeneratedAt = "2026-08-06T00:00:00Z",
            Windows = [.. windows ?? []], Codes = [.. codes ?? []], Provisional = provisional,
        };

    private static PpmWindow Window(string component, string cas, string element, double ppm) =>
        new(component, cas, element,
            new Bound(1, "lod", BoundKinds.Measured, 1), new Bound(500, "cap", BoundKinds.Regulatory, 1),
            ppm, ppm);

    /// Deliberately MINIMAL — one marker is below the 2–3 a real code carries, and `RatioSignature` would
    /// refuse to form a ratio from it. That is fine here and nowhere else: the diff reads ppms and masses and
    /// never touches the signature, so a fuller fixture would add a second substance to every assertion
    /// about one. (<c>RerunDiffWiringTests</c> uses a real two-marker code, because that one round-trips
    /// through the store. If RerunDiff ever starts reading the signature, these fixtures throw — loudly,
    /// which is the failure worth having.)
    private static MarkerCode Code(string component, params CodeMarker[] markers) =>
        new(component, markers, "why");

    [Fact]
    public void OfDosing_WithNoPriorDocument_SaysSo_RatherThanReportingEveryWindowAsNew()
    {
        // Same rule as verdicts. The first dosing a project produces is the baseline, not a change.
        var diff = RerunDiff.OfDosing(null, RerunDiff.DosingSnapshot.Of(
            Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12)])));

        Assert.False(diff.Comparable);
        Assert.Contains("No prior state to compare", diff.Summary);
    }

    [Fact]
    public void AChangedOrderAmountIsReported_EvenWhenEveryPpmIsIdentical()
    {
        // THE test this whole dosing diff exists for. `batchMassKg` is the one amendment whose blast radius is
        // Dosing ALONE (RerunScope.DosingLane), and it is a pure multiplier on the ORDER AMOUNT — it moves no
        // ppm at all. A diff that watched only the ppm windows would answer "nothing changed" to the one
        // amendment guaranteed to land here: a confident all-clear over a procurement quantity that doubled.
        var before = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 12)],
            codes: [Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 100, 200))]));
        var after = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 12)],
            codes: [Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 200, 400))]));

        var change = Assert.Single(RerunDiff.OfDosing(before, after).Changes);
        Assert.Equal(RerunDiff.Kinds.OrderAmountChanged, change.Kind);
        Assert.Equal("Zr (cas-zr) in 'bottle': order amount 200 mg -> 400 mg.", change.Line);
    }

    [Fact]
    public void AChangedRecommendedPpm_NamesTheSubstanceAndBothEnds()
    {
        var before = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12)]));
        var after = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 18.5)]));

        var change = Assert.Single(RerunDiff.OfDosing(before, after).Changes);
        Assert.Equal(RerunDiff.Kinds.PpmChanged, change.Kind);
        Assert.Equal("Zr (cas-zr) in 'bottle': 12 ppm -> 18.5 ppm.", change.Line);
    }

    [Fact]
    public void ADifferenceInvisibleAtDisplayPrecision_ProducesNoLine()
    {
        // The values round-trip through JSON and are re-derived by an agent, so raw double inequality fires on
        // noise. A row reading "12 ppm -> 12 ppm" is worse than silence: the operator hunts for a change that
        // is not there, and the next real line is one they have been trained to distrust.
        var before = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12.0)]));
        var after = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12.00001)]));

        var diff = RerunDiff.OfDosing(before, after);
        Assert.Empty(diff.Changes);
        Assert.True(diff.Comparable);          // it WAS compared — it simply did not move visibly
    }

    [Fact]
    public void TheProvisionalFlagTurningOn_IsReportedAsAnAlarm_AndFirst()
    {
        // Provisional is the ORDER-BLOCKING flag: it changes what the record is ALLOWED to do, not merely what
        // it says. A rerun that turns it on has quietly withdrawn procurement's permission, and burying that
        // under a list of ppm movements is how it gets missed.
        var before = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 12)], provisional: false));
        var after = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 18)], provisional: true));

        var diff = RerunDiff.OfDosing(before, after);
        Assert.Equal(RerunDiff.Kinds.ProvisionalChanged, diff.Changes[0].Kind);
        Assert.Contains("PROVISIONAL", diff.Lines[0]);
        Assert.Equal(2, diff.Changes.Count);
    }

    [Fact]
    public void ASubstanceInTwoCodesForOneComponent_HasItsOrderAmountSummed()
    {
        // Procurement orders the TOTAL. Last-wins would under-report the amount, and under-reporting is the
        // direction that leaves a batch short of the marker nobody can then read.
        var snapshot = RerunDiff.DosingSnapshot.Of(Dosing(codes:
        [
            Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 100, 200)),
            Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 50, 100)),
        ]));

        Assert.Equal(300, Assert.Single(snapshot.Dosed).CompoundMassMg);
    }

    [Fact]
    public void AWindowWithNoFinalizedCode_ReadsAsNotOrdered_NeverAsZeroMilligrams()
    {
        // A missing order amount is not an order amount of zero. Rendering it as "0 mg" would report a
        // substance nobody will order as one nobody NEEDS to order — the absence path presented as a
        // clearance, which is the exact shape of the bugs this codebase has shipped before.
        var before = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 12)],
            codes: [Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 100, 200))]));
        var after = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12)]));

        var change = Assert.Single(RerunDiff.OfDosing(before, after).Changes);
        Assert.Equal(RerunDiff.Kinds.OrderAmountChanged, change.Kind);
        Assert.Equal("not ordered", change.After);
        // The whole point, asserted on the line an operator actually reads. (Not `DoesNotContain("0 mg")` —
        // the BEFORE side is legitimately "200 mg", and that spelling of the assertion passes for the wrong
        // reason on any amount not ending in a zero.)
        Assert.EndsWith("-> not ordered.", change.Line);
    }

    [Fact]
    public void ADroppedSubstance_IsNamed()
    {
        // A substance that was dosed and no longer is has left the codes. Silence here would let a marker
        // vanish from the product between two readings of the same screen.
        var before = RerunDiff.DosingSnapshot.Of(Dosing(windows: [Window("bottle", "cas-zr", "Zr", 12)]));
        var after = RerunDiff.DosingSnapshot.Of(Dosing());

        var change = Assert.Single(RerunDiff.OfDosing(before, after).Changes);
        Assert.Equal(RerunDiff.Kinds.Removed, change.Kind);
        Assert.Contains("NO LONGER DOSED", change.Line);
        Assert.Contains("Zr (cas-zr) in 'bottle'", change.Line);
    }

    [Fact]
    public void AnUnchangedDosing_SaysSo_AndIsNotMistakenForAnIncomparableOne()
    {
        var snapshot = RerunDiff.DosingSnapshot.Of(Dosing(
            windows: [Window("bottle", "cas-zr", "Zr", 12)],
            codes: [Code("bottle", new CodeMarker("cas-zr", "Zr", 12, 0.5, 100, 200))]));

        var diff = RerunDiff.OfDosing(snapshot, snapshot);
        Assert.True(diff.Comparable);
        Assert.Empty(diff.Lines);
        Assert.Contains("no dosing entry changed", diff.Summary);
    }
}
