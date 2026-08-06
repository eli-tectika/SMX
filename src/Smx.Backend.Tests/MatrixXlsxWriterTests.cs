using ClosedXML.Excel;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

/// The wide export. It was rows=substances × columns=components with one glyph per cell; four phases
/// contributing columns makes components row groups instead (redesign spec §5.3).
///
/// Two of these tests are about the fact that THIS FILE LEAVES OUR HANDS. A spreadsheet gets filtered,
/// re-sorted and pasted into other documents, so anything carried only by fill colour is lost, and a blank
/// cell reads as "not filled in yet" to every reader alive.
public class MatrixXlsxWriterTests
{
    private static Bound Measured(double ppm) => new(ppm, "measured background + 3σ", BoundKinds.Measured, 1.0);
    private static Bound Estimated(double ppm) => new(ppm, "formulation-impact estimate", BoundKinds.Estimate, 0.5);

    /// One cleared+dosed row, and one stopped dead at the element gate.
    private static IReadOnlyList<TableRow> Rows() =>
    [
        new("bottle", "cas-zr", "Zr", "neodecanoate",
            new DiscoveryCells("A", true, "stable in melt", 3),
            new RegulatoryCells(VerdictStatus.Pass,
                [new("ElementGate", VerdictStatus.Pass, [new Citation("reg-index", "reach-annex17#e01", "t")], 0.95, "not listed")],
                Determinations.Recommended, Determinations.Recommended, true),
            new DosingCells(Measured(30), Estimated(900), 100, 2.5, ["Acme", "Beta"], []),
            new OutcomeCells("Zr:Cd = 1.00:0.50", true),
            null, null),

        new("bottle", "cas-cd", "Cd", "sulfide",
            new DiscoveryCells("B", false, "high signal", 1),
            new RegulatoryCells(VerdictStatus.Fail,
                [new("ElementGate", VerdictStatus.Fail, [new Citation("reg-index", "reach-annex17#e23", "t")], 0.99, "Cd restricted")],
                Determinations.Rejected, Determinations.Rejected, true),
            null, null,
            Stages.Regulatory, "Cd is restricted under REACH Annex XVII"),
    ];

    private static IXLWorksheet Sheet(string name)
    {
        var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write(Rows())));
        return wb.Worksheet(name);
    }

    [Fact]
    public void Write_PutsComponentsDownTheSide_AndPhasesAcrossTheTop()
    {
        var ws = Sheet("Matrix");

        Assert.Equal("Component", ws.Cell(1, 1).GetString());
        Assert.Equal("CAS", ws.Cell(1, 4).GetString());
        Assert.Equal("bottle", ws.Cell(2, 1).GetString());
        Assert.Equal("Zr", ws.Cell(2, 2).GetString());
        Assert.Equal("Pass", ws.Cell(2, 9).GetString());
        Assert.Equal("Fail", ws.Cell(3, 9).GetString());
    }

    [Fact]
    public void Write_KeepsTheProposalAndTheDeterminationInSeparateColumns()
    {
        // A reader of this file must be able to see that a substance is in on the AGENT'S say-so and nobody
        // else's. One merged column would hide exactly that.
        var ws = Sheet("Matrix");

        Assert.Equal("Proposed", ws.Cell(1, 10).GetString());
        Assert.Equal("Determination", ws.Cell(1, 11).GetString());
    }

    [Fact]
    public void Write_SpellsOutAPpmBoundsProvenanceInTheCell_NotOnlyInAColour()
    {
        // Fill colour does not survive a filter, a re-sort, or a paste into another document. A bare number
        // reads as measured -- which is the one claim an estimated bound is not making.
        var ws = Sheet("Matrix");

        Assert.Contains(BoundKinds.Measured, ws.Cell(2, 14).GetString());
        Assert.Contains(BoundKinds.Estimate, ws.Cell(2, 16).GetString());
    }

    [Fact]
    public void Write_AStoppedRow_SaysStopped_InTheColumnsItNeverReached()
    {
        // NOT blank. In a spreadsheet a blank cell means "not filled in yet" to every reader, and a
        // substance the operator rejected must not be mistaken for one still being worked on.
        var ws = Sheet("Matrix");

        Assert.Equal("stopped", ws.Cell(3, 13).GetString());   // ppm floor
        Assert.Equal("stopped", ws.Cell(3, 21).GetString());   // in code
        Assert.Equal(Stages.Regulatory, ws.Cell(3, 23).GetString());
        Assert.Contains("REACH", ws.Cell(3, 24).GetString());
    }

    [Fact]
    public void Write_ARowThatSimplyHasNotReachedAPhase_ReadsAsNotReached_NotStopped()
    {
        // The pair to the test above, and the distinction the whole projection exists to keep: a phase that
        // has not run yet is a dash, never the word "stopped".
        var pending = new TableRow("bottle", "cas-y", "Y", "oxide",
            new DiscoveryCells("A", false, "documented tracer", 2),
            null, null, null, null, null);

        var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write([pending])));
        var ws = wb.Worksheet("Matrix");

        Assert.NotEqual("stopped", ws.Cell(2, 9).GetString());
        Assert.Equal("", ws.Cell(2, 23).GetString());          // no stopped-at
    }

    [Fact]
    public void Write_ProducesCitationsSheet_OneRowPerDimensionCitation()
    {
        var ws = Sheet("Citations");

        Assert.Equal("bottle", ws.Cell(2, 1).GetString());
        Assert.Equal("cas-zr", ws.Cell(2, 2).GetString());
        Assert.Equal("reach-annex17#e01", ws.Cell(2, 5).GetString());
        Assert.Equal("reach-annex17#e23", ws.Cell(3, 5).GetString());
    }
}
