using ClosedXML.Excel;

namespace Smx.ReferenceData.Transform;

public sealed record SheetRow(IReadOnlyDictionary<string, string> Cells)
{
    public string Get(string header) => Cells.TryGetValue(header, out var v) ? v : "";
}

public static class SheetReader
{
    /// <summary>Reads a worksheet as header-keyed rows. headerRowNumber is 1-based.</summary>
    public static IReadOnlyList<SheetRow> Read(IXLWorksheet ws, int headerRowNumber)
    {
        var used = ws.RangeUsed();
        if (used is null) return Array.Empty<SheetRow>();
        var headerRow = ws.Row(headerRowNumber);
        var headers = new Dictionary<int, string>();
        foreach (var cell in headerRow.CellsUsed())
        {
            var h = cell.GetString().Trim();
            if (h.Length > 0) headers[cell.Address.ColumnNumber] = h;
        }
        var rows = new List<SheetRow>();
        var lastRow = used.LastRow().RowNumber();
        for (int r = headerRowNumber + 1; r <= lastRow; r++)
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool any = false;
            foreach (var (col, header) in headers)
            {
                var val = ws.Cell(r, col).GetString().Replace("\n", " ").Trim();
                cells[header] = val;
                if (val.Length > 0) any = true;
            }
            if (any) rows.Add(new SheetRow(cells));
        }
        return rows;
    }

    /// <summary>Splits a "G15,G26" / "G15 G26" style ref cell into ["G15","G26"].</summary>
    public static IReadOnlyList<string> RefIds(string cell)
        => cell.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Where(t => t.Length > 0).ToList();

    public static double? Num(string cell)
        => double.TryParse(cell, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
