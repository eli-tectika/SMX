using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// The join that makes the whole record one table. Most of these tests are about ONE distinction: an empty
/// cell means either "this row was dropped here" or "this phase has not run yet", and the two must never
/// render alike. Every `StoppedAt` assertion below is paired with a not-yet-run case that must stay null.
public class ProjectTableTests
{
    private const string P = "p1";

    private static CandidatesDoc Candidates(params (string Comp, string Cas, string El)[] subs) => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances = [.. subs.Select(s => new CandidateSubstance(
            s.Comp, s.El, "oxide", s.Cas, null, null, Preferred: s.Cas == "cas-a", "A", "why",
            [new Citation("catalog", "ref-catalog/x", "t")]))],
    };

    private static VerdictDoc Verdict(string comp, string cas, string el,
        VerdictStatus overall = VerdictStatus.Pass,
        string? determination = null, string? proposed = null, string? reason = null) => new()
    {
        Id = RecordIds.Verdict(P, cas, comp), ProjectId = P, Cas = cas, ComponentId = comp,
        Element = el, Form = "oxide", EvidenceReviewed = true,
        Dimensions = [new("ElementGate", overall, [new Citation("regulatory", "x", "t")], 0.9, "r")],
        Determination = determination, DeterminationReason = reason,
        ProposedDetermination = proposed,
    };

    private static DosingDoc Dosing(params (string Comp, string Cas, string El)[] windows) => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t",
        Windows = [.. windows.Select(w => new PpmWindow(w.Comp, w.Cas, w.El,
            new Bound(10, "measured background", BoundKinds.Measured, 1.0),
            new Bound(900, "formulation estimate", BoundKinds.Estimate, 0.5), 100, 30))],
        Codes = [new MarkerCode("bottle",
            [.. windows.Select(w => new CodeMarker(w.Cas, w.El, 100, 0.74, 1.0, 2.5))], "r")],
        Supply = [.. windows.Select(w => new SupplierAudit(w.Cas, w.El, ["Acme"], []))],
    };

    private static Dictionary<string, StageState> Stages(params string[] done)
    {
        var d = new Dictionary<string, StageState>();
        foreach (var s in Records.Stages.All) d[s] = new StageState { Status = StageStatus.Pending };
        foreach (var s in done) d[s] = new StageState { Status = StageStatus.Done };
        return d;
    }

    [Fact]
    public void Build_WithNoCandidates_IsEmpty_NotAnError()
    {
        // Discovery has not produced anything. Building rows from verdicts alone would resurrect substances
        // a revise deliberately orphaned.
        Assert.Empty(ProjectTable.Build(null, [], null, null, Stages()));
    }

    [Fact]
    public void Build_OneRowPerComponentAndCas_OrderedStably()
    {
        var rows = ProjectTable.Build(
            Candidates(("label", "cas-b", "La"), ("bottle", "cas-a", "Ce"), ("bottle", "cas-b", "La")),
            [], null, null, Stages());

        Assert.Equal(
            [("bottle", "cas-a"), ("bottle", "cas-b"), ("label", "cas-b")],
            rows.Select(r => (r.ComponentId, r.Cas)));
    }

    [Fact]
    public void Build_CarriesTheDiscoveryGroup()
    {
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")), [], null, null, Stages()));

        Assert.NotNull(row.Discovery);
        Assert.Equal("A", row.Discovery!.Tier);
        Assert.True(row.Discovery.Preferred);
        Assert.Equal(1, row.Discovery.Sources);
    }

    [Fact]
    public void Build_KeepsTheProposalAndTheDeterminationAsSeparateFields()
    {
        // THE LAW-9 LINE AT THE RENDERING LAYER. Collapsing these into one column would let the agent's
        // opinion be read as the operator's ruling by anyone looking at the table -- which is the whole
        // thing CompliantSet's two fields exist to keep apart.
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", proposed: Determinations.Recommended)],
            null, null, Stages(Records.Stages.Regulatory)));

        Assert.Equal(Determinations.Recommended, row.Regulatory!.ProposedDetermination);
        Assert.Null(row.Regulatory.Determination);
        Assert.Null(row.StoppedAt);
    }

    [Fact]
    public void Build_AnOperatorRejection_StopsTheRow_AtRegulatory_WithTheReason()
    {
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", VerdictStatus.Fail,
                determination: Determinations.Rejected, reason: "element gate failed product-wide")],
            null, null, Stages(Records.Stages.Regulatory)));

        Assert.Equal(Records.Stages.Regulatory, row.StoppedAt);
        Assert.Equal("element gate failed product-wide", row.StoppedReason);
    }

    [Fact]
    public void Build_AProposedRejectionNobodyHasRuledOn_IsNOTStopped()
    {
        // The operator can still overrule it. Marking it stopped would render the agent's proposal as a
        // decision -- and the row would visually disappear from the journey on the agent's say-so alone.
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", VerdictStatus.Fail, proposed: Determinations.Rejected)],
            null, null, Stages(Records.Stages.Regulatory)));

        Assert.Null(row.StoppedAt);
        Assert.Equal(Determinations.Rejected, row.Regulatory!.ProposedDetermination);
    }

    [Fact]
    public void Build_ARowDroppedByARUNDosing_IsStoppedThere_WithTheDocumentsOwnReason()
    {
        var dosing = Dosing();   // no windows at all
        dosing.Provisional = true;
        dosing.ProvisionalReasons = ["no metal loading on file for: cas-a. These substances were left undosed."];

        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", determination: Determinations.Recommended)],
            dosing, null, Stages(Records.Stages.Regulatory, Records.Stages.Dosing)));

        Assert.Equal(Records.Stages.Dosing, row.StoppedAt);
        Assert.Contains("metal loading", row.StoppedReason);
    }

    [Fact]
    public void Build_TheSameRow_IsNOTStopped_WhenDosingHasNotRun()
    {
        // THE PAIR. Byte-for-byte the same absent window as the test above; the only difference is that
        // Dosing has not run. If these two ever agree, "not started" is rendering as "rejected" -- the bug
        // this whole type is shaped to prevent.
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", determination: Determinations.Recommended)],
            null, null, Stages(Records.Stages.Regulatory)));

        Assert.Null(row.StoppedAt);
        Assert.Null(row.Dosing);
    }

    [Fact]
    public void Build_ARowWithNoVerdict_IsStoppedOnlyOnceRegulatoryHasRun()
    {
        var candidates = Candidates(("bottle", "cas-a", "Ce"));

        var notRun = Assert.Single(ProjectTable.Build(candidates, [], null, null, Stages()));
        Assert.Null(notRun.StoppedAt);
        Assert.Null(notRun.Regulatory);

        var ran = Assert.Single(ProjectTable.Build(
            candidates, [], null, null, Stages(Records.Stages.Regulatory)));
        Assert.Equal(Records.Stages.Regulatory, ran.StoppedAt);
        Assert.Contains("no verdict", ran.StoppedReason);
    }

    [Fact]
    public void Build_CarriesTheDosingGroup_WithBothBoundsWhole()
    {
        // The bounds travel WHOLE, Kind included: a measured floor and an estimated one are not the same
        // claim, and a ppm stripped of its provenance is the dangerous rendering (spec §10).
        var row = Assert.Single(ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce")),
            [Verdict("bottle", "cas-a", "Ce", determination: Determinations.Recommended)],
            Dosing(("bottle", "cas-a", "Ce")), null,
            Stages(Records.Stages.Regulatory, Records.Stages.Dosing)));

        Assert.Equal(BoundKinds.Measured, row.Dosing!.Floor.Kind);
        Assert.Equal(BoundKinds.Estimate, row.Dosing.Upper.Kind);
        Assert.Equal(100, row.Dosing.RecommendedPpm);
        Assert.Equal(2.5, row.Dosing.CompoundMassMg);
        Assert.Equal(["Acme"], row.Dosing.Suppliers);
    }

    [Fact]
    public void Build_MarksTheRowInTheVpConfirmedCode_AndOnlyThatCode()
    {
        // TWO markers, because a code IS the ratio between them — RatioSignature refuses to form one from a
        // single marker, and the signature is the identity the whole outcome column is keyed on.
        var dosing = Dosing(("bottle", "cas-a", "Ce"), ("bottle", "cas-b", "La"));
        var decision = new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [], null,
                ConfirmedCode: dosing.Codes[0].RatioSignature)],
            Procurement = new ProcurementState { OrderedCas = ["cas-a"] },
        };

        var rows = ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce"), ("bottle", "cas-b", "La")),
            [Verdict("bottle", "cas-a", "Ce", determination: Determinations.Recommended),
             Verdict("bottle", "cas-b", "La", determination: Determinations.Recommended)],
            dosing, decision,
            Stages(Records.Stages.Regulatory, Records.Stages.Dosing, Records.Stages.Decision));

        var a = rows.Single(r => r.Cas == "cas-a");
        Assert.Equal(dosing.Codes[0].RatioSignature, a.Outcome!.InCode);
        Assert.True(a.Outcome.Ordered);
        Assert.Null(a.StoppedAt);

        // In the code, but NOT ordered — the two are different facts and the table keeps them apart.
        var b = rows.Single(r => r.Cas == "cas-b");
        Assert.Equal(dosing.Codes[0].RatioSignature, b.Outcome!.InCode);
        Assert.False(b.Outcome.Ordered);
    }

    [Fact]
    public void Build_ARowLeftOutOfTheConfirmedCode_IsStoppedAtDecision()
    {
        // It cleared regulatory and got a ppm window, and still did not make the final code. That is a real
        // outcome the table must state, not a blank three columns from the end.
        var dosing = Dosing(("bottle", "cas-a", "Ce"), ("bottle", "cas-b", "La"));
        // The VP confirmed a DIFFERENT code from the one on file, so no row is in it.
        var decision = new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [], null, ConfirmedCode: "Ce:Nd = 1.00:0.25")],
        };

        var rows = ProjectTable.Build(
            Candidates(("bottle", "cas-a", "Ce"), ("bottle", "cas-b", "La")),
            [Verdict("bottle", "cas-a", "Ce", determination: Determinations.Recommended),
             Verdict("bottle", "cas-b", "La", determination: Determinations.Recommended)],
            dosing, decision,
            Stages(Records.Stages.Regulatory, Records.Stages.Dosing, Records.Stages.Decision));

        Assert.All(rows, r =>
        {
            Assert.Equal(Records.Stages.Decision, r.StoppedAt);
            Assert.Contains("not selected", r.StoppedReason);
        });
    }
}
