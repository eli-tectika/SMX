using ClosedXML.Excel;
using Smx.Backend.Api;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class MatrixXlsxWriterTests
{
    private static MatrixDoc Matrix() => new()
    {
        Id = "p1|matrix", ProjectId = "p1",
        Rows = [new("Zr", "neodecanoate", "cas-zr"), new("Cd", "sulfide", "cas-cd")],
        Columns = ["bottle", "liquid"],
        Cells =
        [
            new("cas-zr", "bottle", VerdictStatus.Pass, []),
            new("cas-zr", "liquid", VerdictStatus.Conditional, []),
            new("cas-cd", "bottle", VerdictStatus.Fail,
                [new("ElementGate", VerdictStatus.Fail, [new Citation("reg-index", "reach-annex17#e23", "t")], 0.99, "Cd restricted")]),
            new("cas-cd", "liquid", VerdictStatus.Fail, []),
        ],
        GeneratedAt = "2026-07-08T00:00:00Z",
    };

    [Fact]
    public void Write_ProducesMatrixSheet_RowsSubstances_ColumnsComponents()
    {
        using var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write(Matrix())));
        var ws = wb.Worksheet("Matrix");
        Assert.Equal("bottle", ws.Cell(1, 4).GetString());   // headers: Element|Form|CAS|<components...>
        Assert.Equal("Zr", ws.Cell(2, 1).GetString());
        Assert.Equal("Pass", ws.Cell(2, 4).GetString());
        Assert.Equal("Fail", ws.Cell(3, 4).GetString());
    }

    [Fact]
    public void Write_ProducesCitationsSheet_OneRowPerDimensionCitation()
    {
        using var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write(Matrix())));
        var ws = wb.Worksheet("Citations");
        // header + 1 citation row from the cas-cd/bottle ElementGate dimension
        Assert.Equal("cas-cd", ws.Cell(2, 1).GetString());
        Assert.Equal("reach-annex17#e23", ws.Cell(2, 5).GetString());
    }
}
