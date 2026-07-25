using System.Globalization;

namespace Smx.Domain.Xrf;

/// Rows of cells → proposals. Deliberately deterministic and deliberately dumb: no model, no fuzzy
/// column matching, no unit conversion, no "did you mean". A parser that silently mis-maps a column
/// is worse than one that refuses, because the mis-map is invisible and the refusal is not.
///
/// Knows nothing about files. A later task turns .csv/.xlsx into the rows this takes, which is what
/// keeps every rule here testable without touching a disk.
public static class XrfSheet
{
    public static XrfParseResult Parse(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
            return new XrfParseResult([], ["the file is empty."]);

        var header = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++) index.TryAdd(header[i], i);

        // Header-driven, so column ORDER does not matter — but every column must be present. Guessing
        // at a missing one is the silent mis-map this parser exists to refuse.
        var missing = XrfTemplate.Columns.Where(c => !index.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            return new XrfParseResult([], [
                $"the file is missing these columns: {string.Join(", ", missing)}. " +
                $"Download the template and re-export — the header must contain all " +
                $"{XrfTemplate.Columns.Count} columns, in any order."]);

        var proposals = new List<XrfProposal>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            // Spreadsheets emit trailing blank rows by the hundred; they are not an operator error.
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            proposals.Add(ParseRow(row, index, r + 1));
        }

        return proposals.Count == 0
            ? new XrfParseResult([], [
                "the file has a valid header but no data rows. An empty result rendered as an empty " +
                "table looks exactly like a file that parsed fine, so this is reported instead."])
            : new XrfParseResult(proposals, []);
    }

    private static XrfProposal ParseRow(
        IReadOnlyList<string> row, IReadOnlyDictionary<string, int> index, int rowNumber)
    {
        var problems = new List<string>();

        string Cell(string column) =>
            index.TryGetValue(column, out var i) && i < row.Count ? row[i].Trim() : "";

        var component = Cell(XrfTemplate.Component);
        var element = Cell(XrfTemplate.Element);
        var line = Cell(XrfTemplate.Line);
        var status = Cell(XrfTemplate.Status).ToUpperInvariant();
        var note = Cell(XrfTemplate.SignalNote);

        if (component.Length == 0) problems.Add("component is blank.");
        if (element.Length == 0) problems.Add("element is blank.");
        if (line.Length == 0) problems.Add("line is blank — the emission line is part of the pool entry.");

        if (!XrfProposal.Statuses.Contains(status))
            problems.Add($"status '{Cell(XrfTemplate.Status)}' is not one of " +
                         $"{string.Join(" / ", XrfProposal.Statuses)}.");

        // Anti-rubber-stamping (design §4), caught here rather than at confirmation so the operator
        // sees it while the physicist's file is still in front of them.
        if (status == XrfProposal.Conditional && note.Length == 0)
            problems.Add("a conditional (L) row must carry a signal-character note saying what makes " +
                         "the signal conditional.");

        var (level, levelUnit) = Number(
            Cell(XrfTemplate.BackgroundLevel), Cell(XrfTemplate.BackgroundUnit),
            XrfTemplate.BackgroundLevel, XrfTemplate.BackgroundUnit, problems);
        var (lod, lodUnit) = Number(
            Cell(XrfTemplate.DeviceLod), Cell(XrfTemplate.DeviceLodUnit),
            XrfTemplate.DeviceLod, XrfTemplate.DeviceLodUnit, problems);

        return new XrfProposal(
            rowNumber, component, element, line, status, note.Length == 0 ? null : note,
            level, levelUnit, Cell(XrfTemplate.DeviceModel) is { Length: > 0 } m ? m : null,
            lod, lodUnit, problems);
    }

    /// A value and its unit, or nulls plus a problem. Never a default: an unreadable measurement is
    /// not a zero, and a zero background is itself a measurement (see DetectionFloor).
    private static (double? Value, string? Unit) Number(
        string rawValue, string rawUnit, string valueColumn, string unitColumn, List<string> problems)
    {
        if (rawValue.Length == 0 && rawUnit.Length == 0) return (null, null);

        double? value = null;
        // InvariantCulture, always. Under a comma-decimal locale "12,5" is 12.5 and here it is not a
        // number at all — and a separator read the other way is the 1000x mis-dose IntakeAnswers
        // already refuses. Refusing beats guessing.
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            problems.Add($"{valueColumn} '{rawValue}' is not a number. Use a dot decimal separator " +
                         "and no thousands separator or unit suffix.");
        else if (!double.IsFinite(parsed))
            problems.Add($"{valueColumn} '{rawValue}' is not a finite number.");
        else if (parsed < 0)
            problems.Add($"{valueColumn} is negative ({rawValue}).");
        else value = parsed;

        string? unit = null;
        if (rawUnit.Length == 0)
            problems.Add($"{unitColumn} is blank. The unit is carried, never assumed.");
        else if (!string.Equals(rawUnit, XrfTemplate.Ppm, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{unitColumn} is '{rawUnit}', not '{XrfTemplate.Ppm}'. The detection floor is " +
                         "a ppm value and this system will not convert for you — converting needs a " +
                         "calibration it does not have. Ask the physicist for ppm.");
        else unit = XrfTemplate.Ppm;

        return (value, unit);
    }
}
