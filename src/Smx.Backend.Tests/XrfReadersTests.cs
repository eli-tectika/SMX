using System.Text;
using ClosedXML.Excel;
using Smx.Backend.Xrf;

namespace Smx.Backend.Tests;

public class XrfReadersTests
{
    [Fact]
    public async Task Csv_ReadsRowsAndCells()
    {
        var csv = "component,element\nbottle,Ba\nlid,Sr\n";
        var rows = await XrfReaders.ReadAsync("r.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), default);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["bottle", "Ba"], rows[1]);
    }

    [Fact]
    public async Task Csv_KeepsAQuotedFieldContainingACommaInOnePiece()
    {
        // The signal-character note is free text a physicist writes in prose, so it WILL contain
        // commas. Splitting on every comma would shear the note across columns and shift every
        // column after it — silently producing a row that parses and means something else.
        var csv = "component,signal_note\nbottle,\"shoulder on Ba Ka, resolved at 40 kV\"\n";
        var rows = await XrfReaders.ReadAsync("r.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), default);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal("shoulder on Ba Ka, resolved at 40 kV", rows[1][1]);
    }

    [Fact]
    public async Task Csv_StripsAUtf8Bom_WhichExcelPutsOnTheFirstHeader()
    {
        // Left in place the BOM becomes an invisible prefix on "component", the header lookup misses,
        // and the parser reports a missing column the operator can plainly see is present.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("component\nbottle\n")).ToArray();
        var rows = await XrfReaders.ReadAsync("r.csv", new MemoryStream(bytes), default);
        Assert.Equal("component", rows[0][0]);
    }

    [Fact]
    public async Task Tsv_SplitsOnTabs()
    {
        var tsv = "component\telement\nbottle\tBa\n";
        var rows = await XrfReaders.ReadAsync("r.tsv", new MemoryStream(Encoding.UTF8.GetBytes(tsv)), default);
        Assert.Equal(["bottle", "Ba"], rows[1]);
    }

    [Fact]
    public async Task Xlsx_ReadsTheFirstWorksheet()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("result");
        ws.Cell(1, 1).Value = "component";
        ws.Cell(1, 2).Value = "background_level";
        ws.Cell(2, 1).Value = "bottle";
        ws.Cell(2, 2).Value = 12.5;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var rows = await XrfReaders.ReadAsync("r.xlsx", ms, default);
        Assert.Equal("bottle", rows[1][0]);
        // A numeric cell must reach the parser as an INVARIANT string. ClosedXML's culture-aware
        // formatting would render 12.5 as "12,5" under a comma-decimal locale, and the parser
        // (correctly, invariantly) would then refuse the operator's own valid spreadsheet.
        Assert.Equal("12.5", rows[1][1]);
    }

    [Fact]
    public async Task AnUnsupportedExtension_IsRefusedByName()
    {
        var e = await Assert.ThrowsAsync<XrfFormatException>(() =>
            XrfReaders.ReadAsync("scan.pdf", new MemoryStream(), default));
        Assert.Contains(".pdf", e.Message);
    }
}
