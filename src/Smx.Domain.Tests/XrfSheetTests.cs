using Smx.Domain.Xrf;

namespace Smx.Domain.Tests;

public class XrfSheetTests
{
    private static List<List<string>> Sheet(params string[][] dataRows)
    {
        // NOTE: a bare collection expression cannot appear as an element inside a `{ }` collection
        // initializer (the grammar reserves `{ [expr] ... }` for indexer-initializer syntax), so this
        // is written as a top-level collection expression instead of `new List<List<string>> { [.. Columns] }`.
        List<List<string>> rows = [[.. XrfTemplate.Columns]];
        rows.AddRange(dataRows.Select(r => r.ToList()));
        return rows;
    }

    private static string[] Row(
        string component = "bottle", string element = "Ba", string line = "Ka",
        string status = "V", string note = "", string level = "12.5", string levelUnit = "ppm",
        string device = "Niton XL5", string lod = "3.0", string lodUnit = "ppm") =>
        [component, element, line, status, note, level, levelUnit, device, lod, lodUnit];

    [Fact]
    public void Parse_ReadsAWellFormedRow()
    {
        var result = XrfSheet.Parse(Sheet(Row()));

        Assert.Empty(result.SheetProblems);
        var p = Assert.Single(result.Proposals);
        Assert.Equal("bottle", p.Component);
        Assert.Equal("Ba", p.Element);
        Assert.Equal("Ka", p.Line);
        Assert.Equal("V", p.Status);
        Assert.Equal(12.5, p.BackgroundLevel);
        Assert.Equal("ppm", p.BackgroundUnit);
        Assert.Equal("Niton XL5", p.DeviceModel);
        Assert.Equal(3.0, p.DeviceLod);
        Assert.Empty(p.Problems);
    }

    [Fact]
    public void Parse_TolerantOfColumnORDER_ButNotOfAMissingColumn()
    {
        // Header-driven, not position-driven. A physicist who moves a column has not corrupted the
        // file — but a MISSING column is a file the parser cannot honestly read, and guessing which
        // one it is is exactly the silent mis-mapping this whole approach exists to refuse.
        // Same `{ [...] }` grammar ambiguity as Sheet() above — written as collection expressions.
        List<List<string>> reordered =
        [
            ["element", "component", "line", "status", "signal_note",
             "background_level", "background_unit", "device_model", "device_lod", "device_lod_unit"],
            ["Ba", "bottle", "Ka", "V", "", "12.5", "ppm", "Niton XL5", "3.0", "ppm"],
        ];
        Assert.Equal("bottle", Assert.Single(XrfSheet.Parse(reordered).Proposals).Component);

        List<List<string>> missing = [["component", "element", "line", "status"]];
        var problem = Assert.Single(XrfSheet.Parse(missing).SheetProblems);
        Assert.Contains("signal_note", problem);
        Assert.Contains("background_level", problem);
    }

    [Fact]
    public void Parse_RefusesAUnitThatIsNotPpm_RatherThanConvertingIt()
    {
        // The floor is a ppm value. Converting counts to ppm needs a calibration this code does not
        // have, and counts silently relabelled as ppm is a floor wrong by orders of magnitude — in
        // the direction that ships a marker nobody can read. So: refuse, and say what to do.
        var p = Assert.Single(XrfSheet.Parse(Sheet(Row(levelUnit: "counts"))).Proposals);
        var problem = Assert.Single(p.Problems);
        Assert.Contains("counts", problem);
        Assert.Contains("ppm", problem);
    }

    [Fact]
    public void Parse_RefusesANumberItCannotRead_AndNamesTheCell()
    {
        // "12,5" under a comma-decimal locale is 12.5; read as invariant it is not a number at all.
        // Refusing beats guessing: guessing wrong by 10x is a mis-dose nothing downstream catches.
        var p = Assert.Single(XrfSheet.Parse(Sheet(Row(level: "12,5"))).Proposals);
        Assert.Contains(p.Problems, x => x.Contains("background_level") && x.Contains("12,5"));
    }

    [Fact]
    public void Parse_FlagsAConditionalRowWithNoSignalNote()
    {
        // The anti-rubber-stamping rule (design §4), enforced at the earliest possible moment so the
        // operator sees it while they still have the physicist's file in front of them.
        var p = Assert.Single(XrfSheet.Parse(Sheet(Row(status: "L", note: ""))).Proposals);
        Assert.Contains(p.Problems, x => x.Contains("signal"));
    }

    [Fact]
    public void Parse_AcceptsAnXRow_WhichIsMeasuredAndRejected_NotMissing()
    {
        // X is a measurement, not an omission. Recording it is what distinguishes "the physicist
        // measured Fe and it is all over the background" from "nobody ever looked at Fe".
        var p = Assert.Single(XrfSheet.Parse(Sheet(Row(element: "Fe", status: "X"))).Proposals);
        Assert.Equal("X", p.Status);
        Assert.Empty(p.Problems);
    }

    [Fact]
    public void Parse_RefusesAStatusOutsideTheVocabulary()
    {
        var p = Assert.Single(XrfSheet.Parse(Sheet(Row(status: "maybe"))).Proposals);
        Assert.Contains(p.Problems, x => x.Contains("maybe"));
    }

    [Fact]
    public void Parse_KeepsTheSheetRowNumber_SoAProblemPointsAtTheFile()
    {
        // 1-based and counting the header, because that is what the operator's spreadsheet shows them.
        // A problem on "row 3" that is really row 2 sends them to the wrong line.
        var result = XrfSheet.Parse(Sheet(Row(element: "Ba"), Row(element: "Sr")));
        Assert.Equal(new[] { 2, 3 }, result.Proposals.Select(p => p.RowNumber).ToArray());
    }

    [Fact]
    public void Parse_SkipsBlankRows_WhichSpreadsheetsProduceByTheHundred()
    {
        var rows = Sheet(Row());
        rows.Add(["", "", "", "", "", "", "", "", "", ""]);
        rows.Add([]);
        Assert.Single(XrfSheet.Parse(rows).Proposals);
    }

    [Fact]
    public void Parse_ReportsAnEmptySheet_RatherThanReturningNothingQuietly()
    {
        // An empty result and a successful parse of an empty file are indistinguishable to the screen,
        // and "0 rows found" rendered as a blank table reads as "the file was fine".
        Assert.NotEmpty(XrfSheet.Parse([]).SheetProblems);
        Assert.NotEmpty(XrfSheet.Parse(Sheet()).SheetProblems);
    }

    [Fact]
    public void Template_ParsesAsItsOwnValidInput()
    {
        // The template and the parser share XrfTemplate.Columns, so this cannot drift — but pin it
        // anyway: a template that produces a file the parser rejects is worse than no template.
        var rows = XrfTemplate.Csv
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split(',').ToList())
            .ToList();
        var result = XrfSheet.Parse(rows);
        Assert.Empty(result.SheetProblems);
        Assert.All(result.Proposals, p => Assert.Empty(p.Problems));
    }
}
