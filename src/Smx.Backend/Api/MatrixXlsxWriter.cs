using ClosedXML.Excel;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The project table as a spreadsheet — the wide export (redesign spec §5.3).
///
/// It used to write substances down the side and COMPONENTS across the top, one verdict glyph per cell.
/// That transposition worked only while a cell held a single value; with four phases contributing columns,
/// components become row groups and the phases become the columns.
///
/// This is the artifact a customer gets forwarded, which is why two rules from the UI hold here too and
/// arguably harder:
///
///  - **A stopped row says so, in text, in the columns it never reached.** Blank cells in a spreadsheet are
///    read as "no data yet" by everyone alive; a substance rejected at the element gate must not look like
///    one still being worked on.
///  - **A ppm carries its provenance in the CELL, not in a colour.** Spreadsheets get filtered, re-sorted
///    and pasted into other documents. Fill colour does not survive that journey and a bare number reads as
///    measured — which is exactly the claim an estimated floor is not making.
public static class MatrixXlsxWriter
{
    private const string NotReached = "—";

    public static byte[] Write(IReadOnlyList<TableRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Matrix");

        string[] headers =
        [
            "Component", "Element", "Form", "CAS",
            "Tier", "Preferred", "Why", "Sources",
            "Overall", "Proposed", "Determination", "Evidence reviewed",
            "ppm floor", "Floor basis", "ppm upper", "Upper basis", "Recommended ppm", "Amount (mg)",
            "Suppliers", "Supply risks",
            "In code", "Ordered", "Stopped at", "Why it stopped",
        ];
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var y = r + 2;
            var stopped = row.StoppedAt is not null;

            ws.Cell(y, 1).Value = row.ComponentId;
            ws.Cell(y, 2).Value = row.Element;
            ws.Cell(y, 3).Value = row.Form;
            ws.Cell(y, 4).Value = row.Cas;

            if (row.Discovery is { } d)
            {
                ws.Cell(y, 5).Value = d.Tier;
                ws.Cell(y, 6).Value = d.Preferred ? "yes" : "";
                ws.Cell(y, 7).Value = d.Rationale;
                ws.Cell(y, 8).Value = d.Sources;
            }

            if (row.Regulatory is { } reg)
            {
                var cell = ws.Cell(y, 9);
                cell.Value = reg.Overall.ToString();
                cell.Style.Fill.BackgroundColor = reg.Overall switch
                {
                    VerdictStatus.Pass => XLColor.FromHtml("#c6efce"),
                    VerdictStatus.Conditional => XLColor.FromHtml("#ffeb9c"),
                    VerdictStatus.NeedsReview => XLColor.FromHtml("#d9d2e9"),
                    _ => XLColor.FromHtml("#ffc7ce"),
                };
                // The proposal and the determination are two columns, never one. A reader of this file must
                // be able to see that a substance is in on the AGENT'S say-so and nobody else's.
                ws.Cell(y, 10).Value = reg.ProposedDetermination ?? "";
                ws.Cell(y, 11).Value = reg.Determination ?? "not ruled";
                ws.Cell(y, 12).Value = reg.EvidenceReviewed ? "yes" : "no";
            }
            else FillNotReached(ws, y, 9, 12, stopped);

            if (row.Dosing is { } dose)
            {
                ws.Cell(y, 13).Value = dose.Floor.Ppm;
                ws.Cell(y, 14).Value = BasisOf(dose.Floor);
                ws.Cell(y, 15).Value = dose.Upper.Ppm;
                ws.Cell(y, 16).Value = BasisOf(dose.Upper);
                ws.Cell(y, 17).Value = dose.RecommendedPpm;
                ws.Cell(y, 18).Value = dose.CompoundMassMg;
                ws.Cell(y, 19).Value = string.Join(", ", dose.Suppliers);
                ws.Cell(y, 20).Value = string.Join(", ", dose.Risks);
            }
            else FillNotReached(ws, y, 13, 20, stopped);

            if (row.Outcome is { } o)
            {
                ws.Cell(y, 21).Value = o.InCode ?? "";
                ws.Cell(y, 22).Value = o.Ordered ? "yes" : "";
            }
            else FillNotReached(ws, y, 21, 22, stopped);

            ws.Cell(y, 23).Value = row.StoppedAt ?? "";
            ws.Cell(y, 24).Value = row.StoppedReason ?? "";
            if (stopped) ws.Row(y).Style.Font.FontColor = XLColor.FromHtml("#8a5910");
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        var cit = wb.AddWorksheet("Citations");
        string[] citHeaders =
            ["Component", "CAS", "Dimension", "Source", "Reference", "RetrievedAt", "Status", "Confidence", "Rationale"];
        for (var i = 0; i < citHeaders.Length; i++)
        {
            cit.Cell(1, i + 1).Value = citHeaders[i];
            cit.Cell(1, i + 1).Style.Font.Bold = true;
        }
        var line = 2;
        foreach (var row in rows)
        foreach (var dim in row.Regulatory?.Dimensions ?? [])
        foreach (var c in dim.Citations)
        {
            cit.Cell(line, 1).Value = row.ComponentId; cit.Cell(line, 2).Value = row.Cas;
            cit.Cell(line, 3).Value = dim.Dimension;   cit.Cell(line, 4).Value = c.Source;
            cit.Cell(line, 5).Value = c.Reference;     cit.Cell(line, 6).Value = c.RetrievedAt;
            cit.Cell(line, 7).Value = dim.Status.ToString();
            cit.Cell(line, 8).Value = dim.Confidence;  cit.Cell(line, 9).Value = dim.Rationale;
            line++;
        }
        cit.SheetView.FreezeRows(1);
        cit.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// A ppm bound, with WHERE IT CAME FROM in the same cell. `Kind` is spelled out rather than implied: an
    /// estimate that renders as a bare number is a guess wearing a measurement's clothes, and this file
    /// leaves our hands.
    private static string BasisOf(Bound b) => $"{b.Kind} — {b.Basis}";

    /// A run of columns this row never reached. STOPPED rows get the word, not a dash: in a spreadsheet a
    /// blank means "not filled in yet" to every reader, and a substance the operator rejected must not be
    /// mistaken for one still in progress.
    private static void FillNotReached(IXLWorksheet ws, int y, int from, int to, bool stopped)
    {
        for (var x = from; x <= to; x++) ws.Cell(y, x).Value = stopped ? "stopped" : NotReached;
    }
}
