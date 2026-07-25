using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Smx.Backend.Xrf;

/// The file could not be read at all — as opposed to a row that parsed and is wrong.
public sealed class XrfFormatException(string message) : Exception(message);

/// Files → rows of cells. The ONLY code in the XRF path that knows what a file is; every rule about
/// what the cells MEAN lives in `Smx.Domain.Xrf.XrfSheet`, which is what keeps those rules testable
/// without a disk.
public static class XrfReaders
{
    public static async Task<IReadOnlyList<IReadOnlyList<string>>> ReadAsync(
        string filename, Stream input, CancellationToken ct)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension switch
        {
            ".csv" => await DelimitedAsync(input, ',', ct),
            ".tsv" => await DelimitedAsync(input, '\t', ct),
            ".xlsx" => Xlsx(input),
            _ => throw new XrfFormatException(
                $"'{extension}' files cannot be read here. Save the physicist's result as .csv, .tsv " +
                "or .xlsx — the template download is a .csv you can open in Excel."),
        };
    }

    private static async Task<IReadOnlyList<IReadOnlyList<string>>> DelimitedAsync(
        Stream input, char delimiter, CancellationToken ct)
    {
        // Encoding.UTF8 (not `new UTF8Encoding(false)`) because StreamReader strips a leading BOM that
        // matches the encoding it was GIVEN. Excel puts one on every CSV it writes, and left in place
        // it becomes an invisible prefix on the first header — so the column lookup misses and the
        // parser reports a missing column the operator can see is right there.
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            rows.Add(SplitLine(trimmed, delimiter));
        }
        return rows;
    }

    /// RFC-4180 quoting, minus the parts a single-line reader cannot honour (an embedded newline).
    /// Not optional: `signal_note` is prose a physicist writes, so it WILL contain a comma. Splitting
    /// naively shears the note across two columns and shifts every column after it — producing a row
    /// that still parses and now means something else.
    private static List<string> SplitLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c != '"') { cell.Append(c); continue; }
                // "" inside a quoted field is a literal quote.
                if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private static IReadOnlyList<IReadOnlyList<string>> Xlsx(Stream input)
    {
        using var workbook = new XLWorkbook(input);
        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new XrfFormatException("the workbook has no worksheets.");
        var used = sheet.RangeUsed();
        if (used is null) return [];

        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in used.RowsUsed())
            rows.Add([.. row.Cells().Select(Text)]);
        return rows;
    }

    /// A numeric cell rendered INVARIANTLY. `GetFormattedString()` is culture-aware, so under a
    /// comma-decimal locale it hands back "12,5" — which the (correctly invariant) parser then refuses,
    /// and the operator is told their own valid spreadsheet contains a number that is not a number.
    private static string Text(IXLCell cell) =>
        cell.DataType == XLDataType.Number
            ? cell.GetDouble().ToString(CultureInfo.InvariantCulture)
            : cell.GetFormattedString().Trim();
}
