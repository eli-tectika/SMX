using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DecisionAssemblerTests
{
    private static VerdictDoc Verdict(string cas, string comp, string? det = Determinations.Recommended) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = cas == "cas-zr" ? "Zr" : "Y", Form = "f",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true, Determination = det, DeterminationReason = "ruled",
    };

    private static DosingDoc Dosing() => new()
    {
        Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "t",
        Windows =
        [
            new PpmWindow("bottle", "cas-zr", "Zr", new Bound(10, "m", BoundKinds.Measured, 1.0),
                new Bound(1000, "e", BoundKinds.Estimate, 0.5), 100, 30),
            new PpmWindow("bottle", "cas-y", "Y", new Bound(8, "m", BoundKinds.Measured, 1.0),
                new Bound(800, "e", BoundKinds.Estimate, 0.5), 80, 25),
        ],
        Codes = [new MarkerCode("bottle",
            [new CodeMarker("cas-zr", "Zr", 100, 0.74, 1, 2), new CodeMarker("cas-y", "Y", 80, 0.7, 1, 2)], "r")],
    };

    private static CostDoc Cost() => new()
    {
        Id = RecordIds.Cost("p1"), ProjectId = "p1", GeneratedAt = "t",
        Substances =
        [
            new SupplierAudit("cas-zr", "Zr", ["Acme"], new PriceQuote(1, "USD", "Acme", "25 g",
                new Citation("ref-catalog", "ref-catalog/z", "t")), "ok", []),
            new SupplierAudit("cas-y", "Y", ["Beta"], null, "no price on file — quote required", ["single-source"]),
        ],
    };

    [Fact]
    public void Assemble_FoldsOnlyRecommendedSubstances_WithFullTraceability()
    {
        var rows = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle"), Verdict("cas-ba", "bottle", Determinations.Rejected),
             Verdict("cas-nd", "bottle", det: null)],
            Dosing(), Cost(), ["bottle"]);

        var bottle = Assert.Single(rows);
        Assert.Equal("bottle", bottle.ComponentId);
        // The rejected substance NEVER reaches a decision row — the compliant-set boundary again.
        Assert.DoesNotContain(bottle.Rows, r => r.Cas == "cas-ba");
        // Neither does an UNDETERMINED one: the R.E. has not ruled, so there is nothing to decide over.
        // Only `recommended` folds in — a filter that merely excluded `rejected` would admit this row.
        Assert.DoesNotContain(bottle.Rows, r => r.Cas == "cas-nd");
        var zr = bottle.Rows.Single(r => r.Cas == "cas-zr");
        Assert.Equal(100, zr.RecommendedPpm);
        Assert.True(zr.Cleared.Regulatory && zr.Cleared.Dosing && zr.Cleared.Cost);
        Assert.Equal(RecordIds.Verdict("p1", "cas-zr", "bottle"), zr.Traceability.Verdict);
    }

    [Fact]
    public void Assemble_AnUnpricedSubstance_IsNotClearedForCost_ButStaysOnTheMatrix()
    {
        // "no price on file" is the honest output, not a failure — the row shows, uncleared. Hiding it
        // would push the VP to sign over a substance nobody can order; clearing it would fake a price.
        var rows = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle")], Dosing(), Cost(), ["bottle"]);
        var y = rows.Single().Rows.Single(r => r.Cas == "cas-y");
        Assert.False(y.Cleared.Cost);
        Assert.True(y.Cleared.Regulatory && y.Cleared.Dosing);
    }

    [Fact]
    public void Assemble_ASubstanceWithNoWindow_IsNotClearedForDosing()
    {
        var dosing = Dosing();
        dosing.Windows.RemoveAll(w => w.Cas == "cas-y");
        var y = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle")], dosing, Cost(), ["bottle"])
            .Single().Rows.Single(r => r.Cas == "cas-y");
        Assert.False(y.Cleared.Dosing);
        Assert.Equal(0, y.RecommendedPpm); // no window ⇒ no number; a fabricated ppm here is the harm case
    }
}
