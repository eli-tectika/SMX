using ClosedXML.Excel;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class MatrixXlsxWriter
{
    public static byte[] Write(MatrixDoc matrix)
    {
        using var wb = new XLWorkbook();

        var ws = wb.AddWorksheet("Matrix");
        ws.Cell(1, 1).Value = "Element"; ws.Cell(1, 2).Value = "Form"; ws.Cell(1, 3).Value = "CAS";
        for (var c = 0; c < matrix.Columns.Count; c++) ws.Cell(1, 4 + c).Value = matrix.Columns[c];
        var byCell = matrix.Cells.ToDictionary(x => (x.Cas, x.ComponentId));
        for (var r = 0; r < matrix.Rows.Count; r++)
        {
            var sub = matrix.Rows[r];
            ws.Cell(2 + r, 1).Value = sub.Element; ws.Cell(2 + r, 2).Value = sub.Form; ws.Cell(2 + r, 3).Value = sub.Cas;
            for (var c = 0; c < matrix.Columns.Count; c++)
            {
                var cell = ws.Cell(2 + r, 4 + c);
                var status = byCell[(sub.Cas, matrix.Columns[c])].Overall;
                cell.Value = status.ToString();
                cell.Style.Fill.BackgroundColor = status switch
                {
                    VerdictStatus.Pass => XLColor.FromHtml("#c6efce"),
                    VerdictStatus.Conditional => XLColor.FromHtml("#ffeb9c"),
                    VerdictStatus.NeedsReview => XLColor.FromHtml("#d9d2e9"),
                    _ => XLColor.FromHtml("#ffc7ce"),
                };
            }
        }
        ws.Columns().AdjustToContents();

        var cit = wb.AddWorksheet("Citations");
        string[] headers = ["CAS", "Component", "Dimension", "Source", "Reference", "RetrievedAt", "Status", "Confidence", "Rationale"];
        for (var i = 0; i < headers.Length; i++) cit.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var cell in matrix.Cells)
        foreach (var dim in cell.Dimensions)
        foreach (var c in dim.Citations)
        {
            cit.Cell(row, 1).Value = cell.Cas; cit.Cell(row, 2).Value = cell.ComponentId;
            cit.Cell(row, 3).Value = dim.Dimension; cit.Cell(row, 4).Value = c.Source;
            cit.Cell(row, 5).Value = c.Reference; cit.Cell(row, 6).Value = c.RetrievedAt;
            cit.Cell(row, 7).Value = dim.Status.ToString(); cit.Cell(row, 8).Value = dim.Confidence;
            cit.Cell(row, 9).Value = dim.Rationale;
            row++;
        }
        cit.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
