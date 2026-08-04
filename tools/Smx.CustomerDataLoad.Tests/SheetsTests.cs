using Xunit;
using ClosedXML.Excel;
using Smx.CustomerDataLoad;

namespace Smx.CustomerDataLoad.Tests;

/// The wide→long transposition is the one place a silent mis-read would put a verdict against the
/// wrong element, so it is tested against a workbook shaped exactly like the customer's.
public sealed class SheetsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"smx-test-{Guid.NewGuid():N}.xlsx");

    private void Build(Action<IXLWorksheet, IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        var gold = wb.AddWorksheet("Marker assessment summary");
        var bottle = wb.AddWorksheet("Mix  & Fit clear bottle");
        // Header rows the reader skips (data starts at row 4 / row 2 respectively).
        gold.Cell(1, 1).Value = "Element";
        bottle.Cell(1, 1).Value = "Element";
        fill(gold, bottle);
        wb.SaveAs(_path);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Reads_one_row_per_component_from_a_single_wide_row()
    {
        Build((gold, _) =>
        {
            gold.Cell(4, 1).Value = "Ba";
            gold.Cell(4, 2).Value = "K";
            gold.Cell(4, 3).Value = "V";        // Red Ligure
            gold.Cell(4, 5).Value = "X";        // 9999 Gold granules
            gold.Cell(4, 7).Value = "L";
            gold.Cell(4, 8).Value = "shoulder on the Au line";   // White Ligure + its note
        });

        var rows = Sheets.ReadBackground(_path);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Ba", r.Element));
        Assert.All(rows, r => Assert.Equal("K", r.Line));
        Assert.Equal(["V", "X", "L"], rows.Select(r => r.Status));
        Assert.Equal("shoulder on the Au line", rows[2].Note);
    }

    [Fact]
    public void Check_mark_is_read_as_the_usable_verdict()
    {
        // Sheet 2 writes "usable" as a check mark where sheet 1 writes "V". Read literally, every
        // check mark would drop out of the load and the element would look unassessed.
        Build((_, bottle) =>
        {
            bottle.Cell(2, 1).Value = "K";
            bottle.Cell(2, 2).Value = "K";
            bottle.Cell(2, 3).Value = "✓";
        });

        var rows = Sheets.ReadBackground(_path);

        Assert.Single(rows);
        Assert.Equal("V", rows[0].Status);
        Assert.Equal("Bottle", rows[0].Material);
    }

    [Fact]
    public void Continuation_row_comment_attaches_to_the_element_above()
    {
        // A note that spills onto the next row has no element of its own. Read row-wise it would
        // either vanish or, worse, be taken as belonging to the element that follows it.
        Build((_, bottle) =>
        {
            bottle.Cell(2, 1).Value = "Sc";
            bottle.Cell(2, 2).Value = "K";
            bottle.Cell(2, 3).Value = "L";
            bottle.Cell(2, 7).Value = "very low peak";
            bottle.Cell(3, 7).Value = "on tail of the I peak";   // continuation: no element
            bottle.Cell(4, 1).Value = "Ti";
            bottle.Cell(4, 2).Value = "K";
            bottle.Cell(4, 3).Value = "X";
        });

        var rows = Sheets.ReadBackground(_path);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Sc", rows[0].Element);
        Assert.Contains("very low peak", rows[0].Note);
        Assert.Contains("on tail of the I peak", rows[0].Note);
        Assert.Equal("Ti", rows[1].Element);
        Assert.DoesNotContain("tail of the I peak", rows[1].Note);
    }

    [Fact]
    public void Section_headers_and_unknown_verdicts_are_not_read_as_data()
    {
        Build((gold, _) =>
        {
            gold.Cell(4, 1).Value = "Solvents, media, coating";   // a section header, not an element
            gold.Cell(4, 3).Value = "V";
            gold.Cell(5, 1).Value = "Fe";
            gold.Cell(5, 2).Value = "K";
            gold.Cell(5, 3).Value = "?";                          // not a V/L/X verdict
        });

        Assert.Empty(Sheets.ReadBackground(_path));
    }
}
