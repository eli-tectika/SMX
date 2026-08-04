using ClosedXML.Excel;

namespace Smx.CustomerDataLoad;

/// One verdict: this element, on this emission line, against this material's background.
public sealed record BackgroundRow(string Material, string Element, string Line, string Status, string Note);

/// One historical project from the polymers workbook.
public sealed record PolymerRow(
    string Project, string Material, string Application, string ProductType,
    IReadOnlyList<string> Markers, IReadOnlyList<string> Molecules,
    IReadOnlyList<(string Cas, string Molecule)> CasEntries,
    string MasterbatchPpm, string FinalPpm, string SelectionReason, string Notes, string NextItems);

/// Readers for the two customer workbooks.
///
/// Both are WIDE (a column per material/component) and the record is LONG, so every reader here
/// transposes. The transposition is the risky part and it is done in exactly one place.
public static class Sheets
{
    /// ClosedXML REWRITES a workbook on open (see CLAUDE.md on `data/`). These files are the customer's
    /// originals sitting in a Downloads folder, so every read happens against a throwaway copy — the
    /// source bytes are never touched.
    public static T ReadCopy<T>(string path, Func<IXLWorkbook, T> read)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"smx-load-{Guid.NewGuid():N}.xlsx");
        File.Copy(path, temp, overwrite: true);
        try
        {
            using var wb = new XLWorkbook(temp);
            return read(wb);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort; it is a temp file */ }
        }
    }

    private const string GoldSheet = "Marker assessment summary";
    private const string BottleSheet = "Mix  & Fit clear bottle";   // two spaces, as authored

    /// (label, verdict column, comment column) — 1-based, as ClosedXML counts.
    private static readonly (string Label, int Verdict, int Comment)[] GoldComponents =
    [
        ("Red Ligure (Genia 192, Progold, Ag-Cu-Zn)", 3, 4),
        ("9999 Gold granules (PM, Feb 2025)", 5, 6),
        ("White Ligure (Genia 181, Progold, Pd)", 7, 8),
        ("Solvent/Media/coating", 10, 11),
    ];

    private static readonly (string Label, int Verdict)[] BottleComponents =
    [
        ("Bottle", 3), ("Bottle - on sticker", 4), ("Lid", 5), ("Liquid", 6),
    ];

    public static readonly IReadOnlySet<string> ValidStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "V", "L", "X" };

    /// The sheets write "usable" as both `V` and a check mark. Normalising here rather than at the
    /// call site is what stops one spelling silently dropping out of the load.
    private static string NormaliseStatus(string raw)
    {
        var s = raw.Trim();
        return s is "✓" or "✔" or "v" ? "V" : s.ToUpperInvariant();
    }

    public static IReadOnlyList<BackgroundRow> ReadBackground(string path)
    {
        return ReadCopy(path, wb =>
        {
            var rows = new List<BackgroundRow>();
            rows.AddRange(ReadWide(wb.Worksheet(GoldSheet), firstDataRow: 4,
                GoldComponents.Select(c => (c.Label, c.Verdict, c.Comment)).ToArray()));
            // The bottle sheet has ONE shared comment column (7) rather than one per component.
            rows.AddRange(ReadWide(wb.Worksheet(BottleSheet), firstDataRow: 2,
                BottleComponents.Select(c => (c.Label, c.Verdict, 7)).ToArray()));
            return rows;
        });
    }

    private static List<BackgroundRow> ReadWide(
        IXLWorksheet ws, int firstDataRow, (string Label, int Verdict, int Comment)[] components)
    {
        var rows = new List<BackgroundRow>();
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        List<int> previous = [];   // indices in `rows` emitted for the element above

        for (var r = firstDataRow; r <= last; r++)
        {
            var element = ws.Cell(r, 1).GetString().Trim();

            if (element.Length == 0)
            {
                // A continuation row: a comment with no element of its own, belonging to the element
                // ABOVE. Folding it back is what stops it becoming a nameless entry — or, worse,
                // being read as a verdict for whatever element follows.
                var extra = string.Join(" ", Enumerable.Range(1, 11)
                    .Select(c => ws.Cell(r, c).GetString().Trim())
                    .Where(v => v.Length > 0));
                if (extra.Length > 0)
                    foreach (var i in previous)
                        rows[i] = rows[i] with { Note = string.Join(" | ", new[] { rows[i].Note, extra }.Where(s => s.Length > 0)) };
                continue;
            }

            if (element.Length > 3) continue;   // a section header, e.g. "Solvents, media, coating"

            var line = ws.Cell(r, 2).GetString().Trim();
            var emitted = new List<int>();
            foreach (var (label, verdictCol, commentCol) in components)
            {
                var status = NormaliseStatus(ws.Cell(r, verdictCol).GetString());
                if (status.Length == 0 || !ValidStatuses.Contains(status)) continue;
                emitted.Add(rows.Count);
                rows.Add(new BackgroundRow(label, element, line, status,
                    ws.Cell(r, commentCol).GetString().Trim()));
            }
            if (emitted.Count > 0) previous = emitted;
        }
        return rows;
    }

    private static readonly char[] MultiSeparators = ['\n', ',', '/'];
    private static readonly string[] NoData = ["no data", "not applicable", "n/a", "-", ""];

    private static string Clean(IXLWorksheet ws, int row, int col)
    {
        var s = ws.Cell(row, col).GetString().Trim();
        return NoData.Contains(s, StringComparer.OrdinalIgnoreCase) ? "" : s;
    }

    private static IReadOnlyList<string> SplitMulti(string s) =>
        s.Split(MultiSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<PolymerRow> ReadPolymers(string path)
    {
        return ReadCopy(path, wb =>
        {
            var ws = wb.Worksheets.First();
            var rows = new List<PolymerRow>();
            var last = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = 3; r <= last; r++)      // rows 1-2 are the banner + header
            {
                var project = Clean(ws, r, 1);
                if (project.Length == 0) continue;
                var casField = Clean(ws, r, 7);
                rows.Add(new PolymerRow(
                    Project: project,
                    Material: Clean(ws, r, 2),
                    Application: Clean(ws, r, 11),
                    ProductType: Clean(ws, r, 4),
                    Markers: SplitMulti(Clean(ws, r, 6)),
                    Molecules: SplitMulti(Clean(ws, r, 5)),
                    CasEntries: Cas.Extract(casField),
                    MasterbatchPpm: Clean(ws, r, 9),
                    FinalPpm: Clean(ws, r, 10),
                    SelectionReason: Clean(ws, r, 12),
                    Notes: Clean(ws, r, 17),
                    NextItems: Clean(ws, r, 18)));
            }
            return (IReadOnlyList<PolymerRow>)rows;
        });
    }
}
