# Conversational Intake — Plan 4: XRF Entry on Background

> **STATUS: COMPLETE (2026-07-22).** All 9 tasks executed subagent-driven. `src/Smx.Backend.sln`
> **861 tests** (816 baseline, +45), `src/Smx.Functions.sln` unchanged at 177, `src/smx-web`
> **155 tests / 17 files** (136 baseline, +19). Both solutions build with **zero warnings**,
> `npm run build` clean, working tree clean. All six named properties in Task 9 Step 3 pass, and
> `MockBadge` is still on `Background.tsx` and every other mocked stage screen.
>
> **Corrections this plan needed, found during execution:**
> - **The manual grid could lose a number the operator could see.** `NumberCell` emits `null` for text
>   it cannot parse — `"12,5"` under a comma-decimal habit — but the input still *showed* `12,5`, the
>   row carried no problem, confirm armed, and the record got no background for that element at all.
>   The operator would have watched themselves enter a measurement that was never stored. An
>   unreadable cell is now a row problem, which is what blocks confirm; verified by deleting it and
>   watching both new tests fail. **This is the failure mode the whole plan exists to prevent, and the
>   plan introduced it** — a reminder that "emit null rather than fabricate a 0" is only half a rule.
> - **Two bugs in this plan's own test code:**
>   - A bare collection expression cannot be an element of a `{ }` collection initializer —
>     `new List<List<string>> { [.. Columns] }` fails `CS1003: '=' expected`, because the grammar
>     reserves `{ [expr] … }` for indexer initializers. Written as `List<List<string>> x = [[.. Columns]];`.
>   - `beforeEach` reset `parseXrf`/`confirmXrf` but not `getXrfState`, and vitest does not clear a
>     mock's call log between tests (no `clearMocks` in `vite.config.ts`), so `toHaveBeenCalledTimes(2)`
>     saw 9. No implementation could have passed it.
> - **`XrfProposal.Problems` binds to `null`, not `[]`, when the field is omitted** — it is
>   `IReadOnlyList<string>` on a positional record, and `XrfConfirmation.Build` reads `.Count`. Every
>   test in Task 4 sent `problems: []` explicitly, so nothing covered the shape a hand-written client
>   actually sends. Normalised at the confirm door, with a test that 500s without it.
> - **`Background.tsx`'s `data-provenance="mock"` was resolved by splitting the screen, not by
>   weakening the attribute.** The real XRF zone was lifted *out* of the mock `<section>`, so the hatch
>   and the print warning now cover exactly the fabricated half. The `MockBadge` moved down to label
>   the verdict matrix it was always about.
> - **`ParkSlot` was removed from this screen only.** Its text asserted "no endpoint reports a park
>   state", which Task 5 made false. Replaced with the real `stages.discovery` status and its `error`
>   verbatim. `StageStatusCard` was deliberately not reused: it renders `error` only for `failed`,
>   which would hide the park reason — the one thing the operator needs.
>
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the physicist's measured XRF result a way back into the system now that the creation form is gone — a deterministic parser over a defined column shape, a row-by-row confirmation surface, a manual grid for anything unparseable, and a Discovery stage that *parks* waiting for it instead of failing.

**Architecture:** One template defines the columns; the parser and the downloadable template both read it, so they cannot drift. Parsing is **pure and writes nothing** — proposals live in the browser until the operator confirms. Confirmation is the single writer: it converts proposals into `ElementPools` + `MeasuredBackgrounds` + `Device` on the existing `ConstraintsDoc`, and that write is itself the change-feed event that releases Discovery. Manual rows and parsed rows are the **same type through the same validator**, so a hand-typed number cannot enter by an easier door than a parsed one.

**Tech Stack:** .NET 8 (`Smx.Domain` pure logic, `Smx.Backend` minimal APIs + ClosedXML 0.104.2 for `.xlsx`), React 18 + TypeScript + vitest.

---

## Read this before you touch anything

- **The design:** [`docs/superpowers/specs/2026-07-21-conversational-intake-design.md`](../specs/2026-07-21-conversational-intake-design.md) **§6** is what this plan implements, in full. §2.4 is why the pools are optional at creation in the first place.
- **Plans 1–3, all complete.** The interview creates a project with a confirmed component set and **no physics data at all**. That is the normal case. This plan is the other half of it.
- **`src/Smx.Domain/DetectionFloor.cs`** — read it end to end before writing any validation. It is the consumer of two of the three inputs, and every refusal it makes is a refusal this plan should make *earlier*, while the operator still has the file open.

### Why this data does not go through an agent

`IntakeAnswers` refuses `elementPools` **by name**:

> *"element pools are the physicist's measured XRF background and cannot be changed through chat. If they are wrong, the physicist must re-measure and the operator must re-enter them."*

An LLM transcribing measured numbers is a mechanism by which a shaved background ships a marker under the detection floor that nobody can read in the field. Nothing in this plan puts a model anywhere near these numbers. The parser is deterministic; the confirmation is the operator's.

### Law 4 does not apply to this screen, and that is not an oversight

Every other analytical surface in this app is read-only, because the operator must not hand-edit **agent output**. The XRF grid is the opposite case: it is the operator's transcription of a **human physicist's measurement**, which no agent produced and no agent may alter. It is editable *because* of the same principle that makes the intake brief read-only.

**Write that reasoning in a comment in the manual-grid component.** Someone will otherwise "fix" the inconsistency by making it read-only, and the only entry point for physics data will vanish a second time.

### Five traps

1. **`[FromServices]` is mandatory on every store parameter in a minimal-API handler.** Miss it and routing breaks for **every** route in the app, `/healthz` included. Task 4 adds four endpoints. A regression guard test is included.
2. **A multipart endpoint needs `.DisableAntiforgery()`** in .NET 8 minimal APIs, or the upload 400s with an antiforgery error that reads like a malformed request. `AttachmentEndpoints.cs` is the working example — copy its shape.
3. **`Smx.Backend.Tests` targets `net10.0`; every other project is `net8.0` with `RollForward=Major`.** Do not "fix" it. Endpoint tests go in `Smx.Backend.Tests`; pure-domain tests go in `Smx.Domain.Tests`.
4. **A loose `getByText(/…/i)` regex is not an assertion.** This bit Plan 3 twice: a regex matched the screen's own explanatory copy, so the test passed with the feature deleted. Assert on a specific element (a `data-` hook, or role + accessible name). **Before you commit a frontend test, delete the thing it claims to pin and confirm it fails.**
5. **`Background.tsx` keeps its `MockBadge`.** The verdict matrix on that screen is still fixture data. This plan adds a *real* zone beside it; it does not make the matrix real. Removing that badge is a defect.

**Baselines — write these in your working notes:**
- `src/Smx.Backend.sln`: **816 tests**, 0 warnings.
- `src/Smx.Functions.sln`: **177 tests**, 0 warnings.
- `src/smx-web`: **136 tests, 15 files** (`npm test`). `node_modules` is gitignored — run `npm install` first or `vitest` is not found.

---

## The column contract

One row per (component, element). Ten columns:

```
component,element,line,status,signal_note,background_level,background_unit,device_model,device_lod,device_lod_unit
```

Four decisions worth understanding before you implement them:

**`status` accepts `V`, `L` *and* `X`.** `V` and `L` become `ElementPool` entries; `X` does not — the pool is the list of *usable* elements, and X means "present in the background, avoid". But the X row is still parsed and still recorded as a `MeasuredBackground`, because the alternative is for the physicist to omit the row, and then nothing distinguishes **measured and rejected** from **never measured**. That distinction is the whole reason the dossier has an `unknown` state, and it matters at least as much here.

**The unit is a column, not baked into the header name.** `background_ppm` would be a header a physicist working in counts pastes counts into, and counts silently labelled ppm is a floor that is wrong by orders of magnitude in the direction that ships an unreadable marker. An explicit unit column forces the statement, and a non-`ppm` unit is **refused with an explanation**, never converted — this code does not know the calibration.

**`device_model` repeats on every row and must not vary.** A project targets one deployment device. Two models in one file is a file describing two projects, and the parser says so rather than picking one.

**`device_lod` is per element, not per (component, element).** The same element on two components repeats the LOD; two *different* LODs for one element is refused for the same reason `DetectionFloor` refuses it — silently taking one risks taking the stale, lower value, and low is the direction that ships an unreadable marker.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Xrf/XrfTemplate.cs` | The column contract, once. Header order + the template body. |
| `src/Smx.Domain/Xrf/XrfProposal.cs` | One parsed-or-typed row, with its own problems. The shared shape. |
| `src/Smx.Domain/Xrf/XrfSheet.cs` | Rows of cells → proposals + problems. Pure; no file format knowledge. |
| `src/Smx.Domain/Xrf/XrfConfirmation.cs` | Proposals → pools + backgrounds + device, or a refusal. The single validator. |
| `src/Smx.Backend/Xrf/XrfReaders.cs` | `.csv`/`.tsv`/`.xlsx` → rows of cells. The only file-format code. |
| `src/Smx.Backend/Api/XrfEndpoints.cs` | `GET /projects/{id}/xrf`, `POST …/xrf/parse`, `POST …/xrf/confirm`, `GET /xrf-template.csv` |
| `src/smx-web/src/components/xrf/XrfProposalTable.tsx` | The proposals table + the confirm gate |
| `src/smx-web/src/components/xrf/XrfEntry.tsx` | Upload, template link, manual grid; owns the confirm call |
| Tests | `src/Smx.Domain.Tests/XrfSheetTests.cs`, `XrfConfirmationTests.cs`; `src/Smx.Backend.Tests/XrfEndpointsTests.cs`, `XrfReadersTests.cs`; `src/Smx.Orchestrator.Tests/DiscoveryParkTests.cs`; `src/smx-web/src/components/xrf/XrfProposalTable.test.tsx`, `XrfEntry.test.tsx` |

**Modify:** `src/Smx.Backend/Program.cs` · `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` · `src/smx-web/src/api/types.ts` · `src/smx-web/src/api/client.ts` · `src/smx-web/src/routes/stages/Background.tsx`

---

## Task 1: The column contract and the sheet parser

Pure domain. No file formats, no I/O — this task takes rows of strings and produces proposals.

**Files:**
- Create: `src/Smx.Domain/Xrf/XrfTemplate.cs`, `src/Smx.Domain/Xrf/XrfProposal.cs`, `src/Smx.Domain/Xrf/XrfSheet.cs`
- Test: `src/Smx.Domain.Tests/XrfSheetTests.cs`

- [ ] **Step 1: Write the failing test** — `src/Smx.Domain.Tests/XrfSheetTests.cs`

```csharp
using Smx.Domain.Xrf;

namespace Smx.Domain.Tests;

public class XrfSheetTests
{
    private static List<List<string>> Sheet(params string[][] dataRows)
    {
        var rows = new List<List<string>> { [.. XrfTemplate.Columns] };
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
        var reordered = new List<List<string>>
        {
            ["element", "component", "line", "status", "signal_note",
             "background_level", "background_unit", "device_model", "device_lod", "device_lod_unit"],
            ["Ba", "bottle", "Ka", "V", "", "12.5", "ppm", "Niton XL5", "3.0", "ppm"],
        };
        Assert.Equal("bottle", Assert.Single(XrfSheet.Parse(reordered).Proposals).Component);

        var missing = new List<List<string>> { ["component", "element", "line", "status"] };
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
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter XrfSheetTests
```

Expected: FAIL — `XrfSheet` does not exist.

- [ ] **Step 3: Implement `src/Smx.Domain/Xrf/XrfTemplate.cs`**

```csharp
namespace Smx.Domain.Xrf;

/// The column contract, defined once.
///
/// The parser and the downloadable template both read THIS — because a template that produces a file
/// the parser rejects is worse than shipping no template at all: it teaches the physicist a format
/// that does not work, and the operator gets the blame.
public static class XrfTemplate
{
    public const string Component = "component";
    public const string Element = "element";
    public const string Line = "line";
    public const string Status = "status";
    public const string SignalNote = "signal_note";
    public const string BackgroundLevel = "background_level";
    public const string BackgroundUnit = "background_unit";
    public const string DeviceModel = "device_model";
    public const string DeviceLod = "device_lod";
    public const string DeviceLodUnit = "device_lod_unit";

    public static readonly IReadOnlyList<string> Columns =
    [
        Component, Element, Line, Status, SignalNote,
        BackgroundLevel, BackgroundUnit, DeviceModel, DeviceLod, DeviceLodUnit,
    ];

    /// The unit every number must be in. Not a default and not a fallback — a value in any other unit
    /// is refused, because converting it needs a calibration this code does not have.
    public const string Ppm = "ppm";

    /// A template with two example rows, one of each kind the operator will get wrong: a conditional
    /// row (which needs the signal note) and an X row (measured, rejected, still recorded).
    public static string Csv =>
        string.Join(",", Columns) + "\n" +
        "bottle,Ba,Ka,V,,12.5,ppm,Niton XL5,3.0,ppm\n" +
        "bottle,Sr,Ka,L,shoulder on the Ba Ka line; resolved at 40 kV,8.1,ppm,Niton XL5,2.5,ppm\n" +
        "bottle,Fe,Ka,X,,940.0,ppm,Niton XL5,1.8,ppm\n";
}
```

- [ ] **Step 4: Implement `src/Smx.Domain/Xrf/XrfProposal.cs`**

```csharp
namespace Smx.Domain.Xrf;

/// One row of the physicist's result — parsed from a file, or typed by hand into the manual grid.
///
/// ONE type for both on purpose. A hand-typed number and a parsed number reach the record through the
/// same validator, so there is no easier door: the manual grid is a fallback for unparseable files,
/// not a way around the checks.
///
/// `Problems` is per-row and never fatal on its own — the screen shows every row it read and marks the
/// bad ones, because an operator who can see which three rows failed can fix three cells. One that is
/// told only "the file is invalid" re-exports the whole thing and guesses.
public sealed record XrfProposal(
    int RowNumber,
    string Component,
    string Element,
    string Line,
    string Status,
    string? SignalNote,
    double? BackgroundLevel,
    string? BackgroundUnit,
    string? DeviceModel,
    double? DeviceLod,
    string? DeviceLodUnit,
    IReadOnlyList<string> Problems)
{
    /// V and L are pool statuses — the usable and the conditional. X is a measurement of an element
    /// that is present in the background, which is recorded but is NOT a pool entry.
    public const string Usable = "V";
    public const string Conditional = "L";
    public const string Present = "X";

    public static readonly IReadOnlyList<string> Statuses = [Usable, Conditional, Present];
}

public sealed record XrfParseResult(
    IReadOnlyList<XrfProposal> Proposals,
    /// Problems with the FILE rather than with a row: a missing column, an empty sheet. A sheet
    /// problem means no proposal can be trusted, so the screen must not offer to confirm any of them.
    IReadOnlyList<string> SheetProblems);
```

- [ ] **Step 5: Implement `src/Smx.Domain/Xrf/XrfSheet.cs`**

```csharp
using System.Globalization;

namespace Smx.Domain.Xrf;

/// Rows of cells → proposals. Deliberately deterministic and deliberately dumb: no model, no fuzzy
/// column matching, no unit conversion, no "did you mean". A parser that silently mis-maps a column
/// is worse than one that refuses, because the mis-map is invisible and the refusal is not.
///
/// Knows nothing about files. `XrfReaders` in the backend turns .csv/.xlsx into the rows this takes,
/// which is what keeps every rule here testable without touching a disk.
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
```

- [ ] **Step 6: Run to verify they pass**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter XrfSheetTests
```

Expected: PASS, 11 tests.

- [ ] **Step 7: Commit**

```bash
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
git add -A src/
git commit -m "feat(xrf): the column contract and a parser that refuses rather than guesses"
```

---

## Task 2: The single validator — proposals to record

`XrfConfirmation` is the **only** path from proposals into `ConstraintsDoc`, whether they were parsed or typed. Every refusal `DetectionFloor` makes is made here first, while the operator can still fix it.

**Files:**
- Create: `src/Smx.Domain/Xrf/XrfConfirmation.cs`
- Test: `src/Smx.Domain.Tests/XrfConfirmationTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Xrf;

namespace Smx.Domain.Tests;

public class XrfConfirmationTests
{
    private static XrfProposal P(
        string component = "bottle", string element = "Ba", string line = "Ka",
        string status = "V", string? note = null, double? level = 12.5,
        string? levelUnit = "ppm", string? device = "Niton XL5", double? lod = 3.0,
        string? lodUnit = "ppm", int row = 2) =>
        new(row, component, element, line, status, note, level, levelUnit, device, lod, lodUnit, []);

    private static readonly string[] Components = ["bottle", "lid"];

    [Fact]
    public void Build_TurnsRowsIntoPoolsBackgroundsAndOneDevice()
    {
        var (built, error) = XrfConfirmation.Build(
            [P(element: "Ba"), P(element: "Sr", status: "L", note: "shoulder on Ba Ka", lod: 2.5)],
            Components);

        Assert.Null(error);
        Assert.Equal(2, built!.ElementPools.Count);
        Assert.Equal(2, built.MeasuredBackgrounds.Count);
        Assert.Equal("Niton XL5", built.Device!.Model);
        Assert.Equal(2, built.Device.Lods.Count);
        Assert.Equal("ppm", built.MeasuredBackgrounds[0].Unit);
    }

    [Fact]
    public void Build_DoesNotPutAnXRowInThePool_ButDoesRecordItsBackground()
    {
        // X = present in the background, so it is not a candidate element and must not appear in the
        // pool Discovery screens against. It IS a measurement, so it is recorded: an element measured
        // and rejected must not be indistinguishable from one nobody looked at.
        var (built, error) = XrfConfirmation.Build([P(element: "Fe", status: "X", level: 940.0)], Components);

        Assert.Null(error);
        Assert.Empty(built!.ElementPools);
        Assert.Equal("Fe", Assert.Single(built.MeasuredBackgrounds).Element);
    }

    [Fact]
    public void Build_RefusesAConditionalRowWithNoSignalNote()
    {
        // The gate that makes the L status mean something. Same rule CreateProjectRequest.Validate
        // enforces — this is the door that replaced it.
        var (_, error) = XrfConfirmation.Build([P(status: "L", note: null)], Components);
        Assert.Contains("signal", error!);
    }

    [Fact]
    public void Build_RefusesARowNamingAComponentTheProjectDoesNotHave()
    {
        // A background measured on a component this product does not have is a measurement of nothing.
        // It would sit in the record looking exactly like data while the real component silently has
        // none — and the floor would then be computed without it.
        var (_, error) = XrfConfirmation.Build([P(component: "sleeve")], Components);
        Assert.Contains("sleeve", error!);
    }

    [Fact]
    public void Build_RefusesTwoMeasurementsOfTheSameElementOnTheSameComponent()
    {
        // Exactly what DetectionFloor refuses, refused earlier. Taking "the first" risks taking the
        // stale, LOWER one — and low is the direction that ships a marker nobody can read.
        var (_, error) = XrfConfirmation.Build([P(level: 12.5), P(level: 4.0, row: 3)], Components);
        Assert.Contains("Ba", error!);
        Assert.Contains("bottle", error!);
    }

    [Fact]
    public void Build_RefusesTwoDifferentLodsForOneElement()
    {
        var (_, error) = XrfConfirmation.Build(
            [P(component: "bottle", lod: 3.0), P(component: "lid", lod: 5.0, row: 3)], Components);
        Assert.Contains("Ba", error!);
    }

    [Fact]
    public void Build_AcceptsTheSameLodRepeatedAcrossComponents()
    {
        // The natural shape of the file: one LOD per element, restated on every row it applies to.
        var (built, error) = XrfConfirmation.Build(
            [P(component: "bottle", lod: 3.0), P(component: "lid", lod: 3.0, row: 3)], Components);
        Assert.Null(error);
        Assert.Single(built!.Device!.Lods);
    }

    [Fact]
    public void Build_RefusesTwoDeviceModels()
    {
        // A project targets ONE deployment device. Two models is a file describing two projects.
        var (_, error) = XrfConfirmation.Build(
            [P(device: "Niton XL5"), P(element: "Sr", device: "Vanta M", row: 3)], Components);
        Assert.Contains("Niton XL5", error!);
        Assert.Contains("Vanta M", error!);
    }

    [Fact]
    public void Build_RefusesANonPositiveLod()
    {
        // DetectionFloor's words: a non-positive LOD puts the floor at or below the background the
        // marker has to be seen against.
        var (_, error) = XrfConfirmation.Build([P(lod: 0)], Components);
        Assert.Contains("positive", error!);
    }

    [Fact]
    public void Build_RefusesAUnitThatIsNotPpm()
    {
        var (_, error) = XrfConfirmation.Build([P(levelUnit: "counts")], Components);
        Assert.Contains("counts", error!);
    }

    [Fact]
    public void Build_RefusesARowThatStillCarriesItsOwnProblems()
    {
        // The screen disables confirm while any row has a problem, but the screen is a convenience.
        // A row that failed to parse must not be confirmable by a caller that ignores the screen.
        var bad = P() with { Problems = ["status 'maybe' is not one of V / L / X."] };
        var (_, error) = XrfConfirmation.Build([bad], Components);
        Assert.Contains("row 2", error!);
    }

    [Fact]
    public void Build_RefusesAnEmptyConfirmation()
    {
        // Confirming nothing would write an empty pool set, which reads downstream exactly like "the
        // physicist measured nothing usable" rather than "nobody pressed anything".
        var (_, error) = XrfConfirmation.Build([], Components);
        Assert.NotNull(error);
    }

    [Fact]
    public void Build_AllowsRowsWithNoDeviceAtAll_SoPoolsCanLandBeforeTheDeviceIsKnown()
    {
        // The pools unblock Discovery; the device only matters at Dosing. Making the device mandatory
        // here would hold the whole pipeline for a number the next stage does not need.
        var (built, error) = XrfConfirmation.Build(
            [P(device: null, lod: null, lodUnit: null)], Components);
        Assert.Null(error);
        Assert.Null(built!.Device);
        Assert.Single(built.ElementPools);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter XrfConfirmationTests
```

Expected: FAIL — `XrfConfirmation` does not exist.

- [ ] **Step 3: Implement `src/Smx.Domain/Xrf/XrfConfirmation.cs`**

```csharp
using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain.Xrf;

/// What a confirmation puts into the record.
public sealed record XrfBuild(
    IReadOnlyList<ElementPool> ElementPools,
    IReadOnlyList<MeasuredBackground> MeasuredBackgrounds,
    XrfDevice? Device);

/// The single door from proposals into `ConstraintsDoc`.
///
/// Every refusal here is a refusal `DetectionFloor` would otherwise make LATER — at dosing, days
/// after the physicist's file was closed and the operator has stopped thinking about it. Duplicate
/// measurements, non-ppm units, a non-positive LOD: all of them are cheap to fix now and expensive to
/// diagnose then. So they are made here, in the operator's words, against the row they came from.
///
/// Returns (build, null) or (null, reason). NEVER throws: the caller is an HTTP handler and the reason
/// is what the operator reads.
public static class XrfConfirmation
{
    public static (XrfBuild? Build, string? Error) Build(
        IReadOnlyList<XrfProposal> proposals, IReadOnlyList<string> componentIds)
    {
        if (proposals.Count == 0)
            return (null, "there is nothing to confirm. Confirming an empty result would record that " +
                          "the physicist found no usable element, which is a very different claim from " +
                          "nobody having entered anything.");

        // A row the parser could not read must not enter by an unchecked caller. The screen blocks it
        // too, but the screen is a convenience and this is the contract.
        if (proposals.FirstOrDefault(p => p.Problems.Count > 0) is { } broken)
            return (null, $"row {broken.RowNumber} still has an unresolved problem: " +
                          $"{broken.Problems[0]} Fix it in the grid, or remove the row.");

        var declared = componentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var p in proposals)
        {
            if (!declared.Contains(p.Component))
                return (null, $"row {p.RowNumber} measures component '{p.Component}', which this " +
                              $"project does not have (it has {string.Join(", ", componentIds)}). A " +
                              "measurement of a component that does not exist would sit in the record " +
                              "looking like data while the real component silently has none.");

            if (!XrfProposal.Statuses.Contains(p.Status))
                return (null, $"row {p.RowNumber} has status '{p.Status}', which is not one of " +
                              $"{string.Join(" / ", XrfProposal.Statuses)}.");

            // Anti-rubber-stamping (design §4). The same rule CreateProjectRequest.Validate enforced;
            // this endpoint is the door that replaced it, so the rule moves with it.
            if (p.Status == XrfProposal.Conditional && string.IsNullOrWhiteSpace(p.SignalNote))
                return (null, $"row {p.RowNumber} ({p.Element} on '{p.Component}') is conditional (L) " +
                              "but carries no signal-character note. A conditional verdict with no " +
                              "stated reason cannot be reviewed, only nodded at.");

            if (p.BackgroundUnit is { } bu && !string.Equals(bu, XrfTemplate.Ppm, StringComparison.OrdinalIgnoreCase))
                return (null, $"row {p.RowNumber} records a background in '{bu}', not ppm.");
            if (p.DeviceLodUnit is { } lu && !string.Equals(lu, XrfTemplate.Ppm, StringComparison.OrdinalIgnoreCase))
                return (null, $"row {p.RowNumber} records a LOD in '{lu}', not ppm.");

            if (p.BackgroundLevel is { } lvl && (!double.IsFinite(lvl) || lvl < 0))
                return (null, $"row {p.RowNumber} has a background of {Num(lvl)}, which is not a " +
                              "non-negative finite number.");
            if (p.DeviceLod is { } lod && (!double.IsFinite(lod) || lod <= 0))
                return (null, $"row {p.RowNumber} has a LOD of {Num(lod)}. It must be positive: a " +
                              "non-positive LOD would put the detection floor at or below the " +
                              "background the marker has to be seen against.");
        }

        // Duplicates refuse rather than resolve. DetectionFloor's reasoning, applied one step earlier:
        // silently taking one of two measurements risks taking the stale, LOWER one, and low is the
        // direction that ships an unreadable marker.
        var dupe = proposals
            .Where(p => p.BackgroundLevel is not null)
            .GroupBy(p => (p.Component, p.Element))
            .FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null)
            return (null, $"{dupe.Key.Element} on '{dupe.Key.Component}' is measured more than once " +
                          $"(rows {string.Join(", ", dupe.Select(p => p.RowNumber))}). Keep only the " +
                          "current measurement — taking one of two silently risks taking the stale one.");

        var models = proposals.Select(p => p.DeviceModel)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToList();
        if (models.Count > 1)
            return (null, $"the rows name more than one XRF device ({string.Join(", ", models)}). A " +
                          "project targets ONE deployment device — the floor is computed against the " +
                          "unit that must read the marker in the field.");

        var lodConflict = proposals
            .Where(p => p.DeviceLod is not null)
            .GroupBy(p => p.Element, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Select(p => p.DeviceLod!.Value).Distinct().Count() > 1);
        if (lodConflict is not null)
            return (null, $"{lodConflict.Key} is given more than one LOD " +
                          $"({string.Join(", ", lodConflict.Select(p => Num(p.DeviceLod!.Value)).Distinct())}). " +
                          "One element on one device has one LOD; keep the current one.");

        var pools = proposals
            .Where(p => p.Status is XrfProposal.Usable or XrfProposal.Conditional)
            .Select(p => new ElementPool(p.Component, p.Element, p.Line, p.Status, p.SignalNote))
            .ToList();

        var backgrounds = proposals
            .Where(p => p.BackgroundLevel is not null)
            .Select(p => new MeasuredBackground(
                p.Component, p.Element, p.BackgroundLevel!.Value, XrfTemplate.Ppm))
            .ToList();

        // The device is OPTIONAL, and deliberately so: the pools are what release Discovery, and the
        // device only matters at Dosing. Requiring it here would hold the whole pipeline for a number
        // the next stage does not read.
        XrfDevice? device = null;
        if (models.Count == 1)
        {
            var lods = proposals
                .Where(p => p.DeviceLod is not null)
                .GroupBy(p => p.Element, StringComparer.Ordinal)
                .Select(g => new DeviceLod(g.Key, g.First().DeviceLod!.Value, XrfTemplate.Ppm))
                .ToList();
            device = new XrfDevice(models[0]!, lods);
        }

        return (new XrfBuild(pools, backgrounds, device), null);
    }

    /// InvariantCulture, for the same reason DetectionFloor uses it: a number in a message the
    /// operator reads must mean one thing everywhere.
    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter XrfConfirmationTests
```

Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(xrf): one validator, refusing at confirmation what DetectionFloor would refuse at dosing"
```

---

## Task 3: The file readers

The only code in this plan that knows what a file is.

**Files:**
- Create: `src/Smx.Backend/Xrf/XrfReaders.cs`
- Test: `src/Smx.Backend.Tests/XrfReadersTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter XrfReadersTests
```

Expected: FAIL — `XrfReaders` does not exist.

- [ ] **Step 3: Implement `src/Smx.Backend/Xrf/XrfReaders.cs`**

```csharp
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
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter XrfReadersTests
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(xrf): csv/tsv/xlsx readers, quote-aware and culture-proof"
```

---

## Task 4: The endpoints

**Files:**
- Create: `src/Smx.Backend/Api/XrfEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/XrfEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

Follow the host-building shape `IntakeBriefEndpointsTests` uses (`IClassFixture<WebApplicationFactory<Program>>` + a `NewApp(...)` helper via `WithWebHostBuilder`) — **read that file first and match its real signature.**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Xrf;

namespace Smx.Backend.Tests;

public class XrfEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public XrfEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IRecordStore store) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)));

    private static async Task<InMemoryRecordStore> StoreWithProject()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
            Components = [new("bottle", "PET", "food contact", ["EU"], "brand")],
        });
        return store;
    }

    private static MultipartFormDataContent File(string name, string content)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        return form;
    }

    private const string GoodCsv =
        "component,element,line,status,signal_note,background_level,background_unit," +
        "device_model,device_lod,device_lod_unit\n" +
        "bottle,Ba,Ka,V,,12.5,ppm,Niton XL5,3.0,ppm\n";

    [Fact]
    public async Task Parse_ReturnsProposals_AndWritesNothing()
    {
        // The parse endpoint is PURE. Two entry points that both write physics data would mean two
        // places a partial background can live, and "which of these is the real one" is a question
        // this system must never have to answer.
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/xrf/parse", File("r.csv", GoodCsv));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ba", body.GetProperty("proposals")[0].GetProperty("element").GetString());
        Assert.Empty((await store.GetConstraintsAsync("proj-1"))!.ElementPools);
    }

    [Fact]
    public async Task Parse_ReportsAnUnreadableFileAsAProblem_NotAsA500()
    {
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/xrf/parse", File("scan.pdf", "%PDF"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Confirm_WritesPoolsBackgroundsAndDeviceOntoTheConstraints()
    {
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsJsonAsync("/projects/proj-1/xrf/confirm", new
        {
            proposals = new[] { new
            {
                rowNumber = 2, component = "bottle", element = "Ba", line = "Ka", status = "V",
                signalNote = (string?)null, backgroundLevel = 12.5, backgroundUnit = "ppm",
                deviceModel = "Niton XL5", deviceLod = 3.0, deviceLodUnit = "ppm",
                problems = Array.Empty<string>(),
            } },
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var c = (await store.GetConstraintsAsync("proj-1"))!;
        Assert.Equal("Ba", Assert.Single(c.ElementPools).Element);
        Assert.Equal(12.5, Assert.Single(c.MeasuredBackgrounds).Level);
        Assert.Equal("Niton XL5", c.Device!.Model);
    }

    [Fact]
    public async Task Confirm_RefusesAConditionalRowWithNoNote_AndWritesNothing()
    {
        // The anti-rubber-stamping rule at the door that replaced the creation form. The "writes
        // nothing" half is the point: a partial write would leave the record holding some of a
        // refused confirmation.
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsJsonAsync("/projects/proj-1/xrf/confirm", new
        {
            proposals = new[] { new
            {
                rowNumber = 2, component = "bottle", element = "Sr", line = "Ka", status = "L",
                signalNote = (string?)null, backgroundLevel = 8.1, backgroundUnit = "ppm",
                deviceModel = "Niton XL5", deviceLod = 2.5, deviceLodUnit = "ppm",
                problems = Array.Empty<string>(),
            } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty((await store.GetConstraintsAsync("proj-1"))!.ElementPools);
    }

    [Fact]
    public async Task Confirm_ReplacesAnEarlierConfirmation_RatherThanAppendingToIt()
    {
        // A re-measure is a CORRECTION, not an addition. Appending would leave two measurements of the
        // same element in the record — which DetectionFloor then refuses to compute a floor from, so
        // the operator's fix would break dosing instead of repairing it.
        var store = await StoreWithProject();
        using var app = NewApp(store);
        var client = app.CreateClient();

        object Body(double level) => new
        {
            proposals = new[] { new
            {
                rowNumber = 2, component = "bottle", element = "Ba", line = "Ka", status = "V",
                signalNote = (string?)null, backgroundLevel = level, backgroundUnit = "ppm",
                deviceModel = "Niton XL5", deviceLod = 3.0, deviceLodUnit = "ppm",
                problems = Array.Empty<string>(),
            } },
        };

        await client.PostAsJsonAsync("/projects/proj-1/xrf/confirm", Body(12.5));
        await client.PostAsJsonAsync("/projects/proj-1/xrf/confirm", Body(9.0));

        var c = (await store.GetConstraintsAsync("proj-1"))!;
        Assert.Equal(9.0, Assert.Single(c.MeasuredBackgrounds).Level);
    }

    [Fact]
    public async Task Confirm_IsA404_WhenTheProjectHasNoConstraintsYet()
    {
        // Constraints are written by the intake agent. No constraints means intake has not run, and
        // there is no component list to validate a component name against.
        using var app = NewApp(new InMemoryRecordStore());
        var res = await app.CreateClient().PostAsJsonAsync(
            "/projects/nope/xrf/confirm", new { proposals = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsWhatIsAlreadyConfirmed_SoTheScreenCanShowIt()
    {
        var store = await StoreWithProject();
        var c = (await store.GetConstraintsAsync("proj-1"))!;
        c.ElementPools = [new("bottle", "Ba", "Ka", "V")];
        await store.UpsertConstraintsAsync(c);
        using var app = NewApp(store);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/projects/proj-1/xrf");

        Assert.Equal("Ba", body.GetProperty("elementPools")[0].GetProperty("element").GetString());
    }

    [Fact]
    public async Task Template_IsServedAsADownloadableCsv_MatchingTheParsersColumns()
    {
        using var app = NewApp(new InMemoryRecordStore());
        var res = await app.CreateClient().GetAsync("/xrf-template.csv");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.StartsWith(string.Join(",", XrfTemplate.Columns), text);
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheXrfSurface()
    {
        // A missing [FromServices] on any store parameter breaks routing for the WHOLE app, and that
        // failure shows up nowhere else.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter XrfEndpointsTests
```

Expected: FAIL — 404 on the new routes.

- [ ] **Step 3: Implement `src/Smx.Backend/Api/XrfEndpoints.cs`**

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Xrf;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Xrf;

namespace Smx.Backend.Api;

/// The physicist's measured XRF result, entered by the operator.
///
/// Removing the creation form removed the only way this data could reach the record. It does NOT come
/// back through chat: `IntakeAnswers` refuses element pools by name, because a model transcribing
/// measured numbers is a mechanism by which a shaved background ships a marker under the detection
/// floor that nobody can read in the field.
///
/// Two endpoints, one of which writes. `parse` is pure — it reads a file and hands back proposals,
/// touching nothing. `confirm` is the single writer. Keeping them separate is what makes the
/// operator's confirmation a real act rather than a consequence of choosing a file.
public sealed record XrfConfirmRequest(List<XrfProposal> Proposals);

public static class XrfEndpoints
{
    public static void MapXrfEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at
        // the top of ProjectEndpoints. Without it, minimal APIs mis-infer it as a body param and break
        // routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/xrf", async (
            string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetConstraintsAsync(projectId, ct) is { } c
                ? Results.Json(new
                {
                    components = c.Components.Select(x => x.Id),
                    elementPools = c.ElementPools,
                    measuredBackgrounds = c.MeasuredBackgrounds,
                    device = c.Device,
                }, Json.Options)
                : Results.NotFound());

        app.MapPost("/projects/{projectId}/xrf/parse", async (
            string projectId, IFormFile file,
            [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.UnprocessableEntity(new { error = "no file was uploaded" });
            if (await store.GetConstraintsAsync(projectId, ct) is null) return Results.NotFound();

            IReadOnlyList<IReadOnlyList<string>> rows;
            try
            {
                await using var stream = file.OpenReadStream();
                rows = await XrfReaders.ReadAsync(file.FileName, stream, ct);
            }
            catch (XrfFormatException e)
            {
                // A file we cannot open is the operator's problem to fix, not a server error. A 500
                // here would read as "the system is broken" when the answer is "save it as .csv".
                return Results.UnprocessableEntity(new { error = e.Message });
            }

            var result = XrfSheet.Parse(rows);
            return result.SheetProblems.Count > 0
                ? Results.UnprocessableEntity(new { error = string.Join(" ", result.SheetProblems) })
                : Results.Json(result, Json.Options);
        }).DisableAntiforgery(); // multipart in a .NET 8 minimal API; see AttachmentEndpoints.

        app.MapPost("/projects/{projectId}/xrf/confirm", async (
            string projectId, XrfConfirmRequest req,
            [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetConstraintsAsync(projectId, ct) is not { } constraints)
                return Results.NotFound();

            var (built, error) = XrfConfirmation.Build(
                req.Proposals, [.. constraints.Components.Select(c => c.Id)]);
            if (error is not null) return Results.UnprocessableEntity(new { error });

            // REPLACE, never append. A re-measure is a correction: appending would leave two
            // measurements of the same element in the record, which DetectionFloor then refuses to
            // compute a floor from — so the operator's fix would break dosing instead of repairing it.
            constraints.ElementPools = [.. built!.ElementPools];
            constraints.MeasuredBackgrounds = [.. built.MeasuredBackgrounds];
            if (built.Device is not null) constraints.Device = built.Device;

            // This write IS the dispatch. The change feed picks up the constraints document and
            // StageDispatcher.OnConstraintsAsync runs Discovery — which, until this moment, was parked
            // precisely because there were no pools to screen against.
            await store.UpsertConstraintsAsync(constraints, ct);

            return Results.Accepted($"/projects/{projectId}", new
            {
                projectId,
                pools = built.ElementPools.Count,
                backgrounds = built.MeasuredBackgrounds.Count,
                device = built.Device?.Model,
            });
        });

        // Served from the same constant the parser reads, so the template cannot drift from what the
        // parser accepts.
        app.MapGet("/xrf-template.csv", () =>
            Results.Text(XrfTemplate.Csv, "text/csv"));
    }
}
```

In `src/Smx.Backend/Program.cs`, call `app.MapXrfEndpoints();` beside the other `Map*Endpoints()` calls.

- [ ] **Step 4: Run to verify they pass, then the whole suite**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter XrfEndpointsTests
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
dotnet test src/Smx.Backend.sln
```

Expected: 9 new tests pass; suite at 816 + 11 + 13 + 6 + 9 = **855**.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(xrf): parse, confirm, read and template endpoints"
```

---

## Task 5: Discovery parks instead of failing

Today an interview-created project with no pools runs the Discovery agent, and `DiscoveryAgent.Validate` rejects **every** candidate — `poolByComponent` is empty, so nothing can be in a pool. The operator gets `needs-review` reading *"candidate element 'Ba' is not in the element pool for component 'bottle'"*: an internal validator message that names a cause the operator cannot act on, after a real LLM call that could never have succeeded.

Design §6 and `Start_SucceedsWithNoElementPools` both say the stage **parks**. Nothing implements it. This task does.

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Test: `src/Smx.Orchestrator.Tests/DiscoveryParkTests.cs`

- [ ] **Step 1: Write the failing test**

This copies `StageDispatcherTests.Sut()` verbatim — read that file to confirm it still looks like this. The dispatcher is driven through **`OnRecordChangedAsync`**, and `FakeAgentRuns` already exposes a `DiscoveryCalls` counter, so no fake needs changing.

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DiscoveryParkTests
{
    private static (StageDispatcher, InMemoryRecordStore, FakeAgentRuns) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents);
    }

    private static async Task<ConstraintsDoc> Seed(InMemoryRecordStore store)
    {
        await store.UpsertProjectAsync(
            ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement));
        return new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "PET", "food contact", ["EU"], "brand")],
            ElementPools = [],
        };
    }

    [Fact]
    public async Task Discovery_ParksWithoutCallingTheAgent_WhenThereAreNoElementPools()
    {
        // The park design §6 promises. Running the agent here cannot succeed: DiscoveryAgent.Validate
        // requires every candidate's element to be in the pool for its component, and with no pools
        // that rejects EVERY candidate. So it burns a real model call to arrive at a message about
        // pools — which is the thing the operator was going to be asked for anyway.
        var (d, store, agents) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        var stage = (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery];
        Assert.Equal("needs-review", stage.Status);
        Assert.Contains("XRF", stage.Error!);
        Assert.Equal(0, agents.DiscoveryCalls);
    }

    [Fact]
    public async Task TheParkDoesNotCountAsAnAttempt()
    {
        // `attempts` is what StageStatusCard renders as "retried Nx". A park is not a try that failed
        // — showing it as one tells the operator the agent has been struggling when it never ran.
        var (d, store, _) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        Assert.Equal(0, (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Attempts);
    }

    [Fact]
    public async Task ThePark_NamesTheScreenTheOperatorHasToGoTo()
    {
        // stage.Error is rendered verbatim to the operator. "Waiting on physics" with no instruction
        // is a dead end: the one thing they need is where to put the file.
        var (d, store, _) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        Assert.Contains("Background", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Error!);
    }

    [Fact]
    public async Task Discovery_RunsNormally_OnceThePoolsAreConfirmed()
    {
        // The other half: the park must LIFT. Confirming pools upserts the constraints document, the
        // change feed delivers it here again, and this time the agent runs. Without this, the entry
        // surface would record the physics and nothing would ever happen.
        var (d, store, agents) = Sut();
        var constraints = await Seed(store);
        constraints.ElementPools = [new("bottle", "Zr", "Ka", "V", null)];

        await d.OnRecordChangedAsync(constraints, default);

        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Fact]
    public async Task KnownCandidateMode_IsNotParked()
    {
        // Provided candidates bypass the Discovery agent entirely, so they never meet the pool check.
        // Parking them would break the eval harness for a precondition their path does not have.
        var (d, store, _) = Sut();
        var constraints = await Seed(store);
        constraints.ProvidedCandidates =
            [new("bottle", "Ba", "barium sulfate", "7727-43-7", null, null, true, "A", "known",
                 [new Citation("catalog", "ref-catalog/x", "t")])];

        await d.OnRecordChangedAsync(constraints, default);

        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }
}
```

Note: `FakeAgentRuns`'s default `Intake` writes constraints that already carry an element pool, so
`StageDispatcherTests.ConstraintsWritten_RunsDiscovery_WritesCandidates` is unaffected by the park. If
any existing dispatcher test *does* break, read it before changing it — it was pinning the old
behaviour, and the fix belongs in the test, not in the park.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter DiscoveryParkTests
```

Expected: FAIL — Discovery runs and lands in `needs-review` with the validator's message, and `DiscoveryWasCalled` is true.

- [ ] **Step 3: Implement the park**

In `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`, inside `OnConstraintsAsync`, **immediately after the candidates idempotency guard and before `SetStageAsync(… "running" … Attempts++)`**:

```csharp
        // PARK, rather than run an agent that cannot succeed.
        //
        // DiscoveryAgent.Validate requires every candidate's element to be in the element pool for its
        // component. With no pools that rejects EVERY candidate, so a real model call burns to arrive
        // at a message about pools — which is exactly what the operator is about to be asked for. And
        // the message it arrives at ("candidate element 'Ba' is not in the element pool for component
        // 'bottle'") names an internal invariant, not the thing the operator can do something about.
        //
        // Provided candidates are exempt: they bypass the Discovery agent entirely (below) and so
        // never meet the pool check.
        //
        // Attempts is deliberately NOT incremented. The UI renders it as "retried Nx", and a park is
        // not a try that failed — it is a try that has not happened.
        if (c.ElementPools.Count == 0 && c.ProvidedCandidates.Count == 0)
        {
            await SetStageAsync(c.ProjectId, Stages.Discovery, s =>
            {
                s.Status = "needs-review";
                s.Error = "waiting on the physicist's XRF measurement. Discovery screens candidate " +
                          "elements against the measured background, and this project has none yet — " +
                          "so there is nothing to screen against and every candidate would be " +
                          "rejected. Enter the XRF result on the Background stage; Discovery starts " +
                          "by itself as soon as it is confirmed.";
            }, ct);
            return;
        }
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter DiscoveryParkTests
dotnet test src/Smx.Backend.sln
```

Expected: 5 new tests pass; **860** total. If an existing dispatcher test breaks because it relied on Discovery running with empty pools, read it: it was pinning the old behaviour. Update it and **say so in your report**.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(discovery): park on an absent XRF background instead of failing against it"
```

---

## Task 6: Frontend types and client

**Files:**
- Modify: `src/smx-web/src/api/types.ts`, `src/smx-web/src/api/client.ts`

- [ ] **Step 1: Fix a wrong comment before you build on it**

`src/smx-web/src/api/types.ts` documents `ElementPool` as:

> `"V" = present/verified`

That is backwards, or at best fatally ambiguous. `Background.tsx`'s own legend is the authority:

- `V` — **not detected** — usable
- `L` — weak signal — conditional
- `X` — **present in background** — avoid

Anyone implementing a parser from the current comment inverts V and X, and the system then selects a marker on an element that is *sitting in the background* — an unreadable marker, which is the exact harm this subsystem exists to prevent. Replace it:

```ts
/**
 * ElementPool — src/Smx.Domain/Records/ConstraintsDoc.cs.
 *
 * One element on one component, after the physicist's XRF background has been interpreted. The pool
 * is the list of elements that are USABLE as markers:
 *   V — not detected in the background, so a marker on it can be read.
 *   L — a weak or interfered signal: conditional, and MUST carry a signal-character note (the backend
 *       rejects an L with a blank note — design §4, anti-rubber-stamping).
 * An element that IS present in the background is "X" and is deliberately absent from the pool; its
 * measurement is still recorded as a MeasuredBackground, so "measured and rejected" stays
 * distinguishable from "never measured".
 *
 * This is measured data. It cannot be edited through chat (IntakeAnswers refuses it by name) — it is
 * entered on the Background stage and re-entered if the physicist re-measures.
 */
```

- [ ] **Step 2: Add the types**

```ts
/** MeasuredBackground — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface MeasuredBackground {
  component: string;
  element: string;
  level: number;
  /** Carried, never assumed — which is why this is not called `levelPpm`. */
  unit: string;
}

/** DeviceLod / XrfDevice — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface DeviceLod {
  element: string;
  lod: number;
  unit: string;
}

export interface XrfDevice {
  model: string;
  lods: DeviceLod[];
}

/**
 * XrfProposal — src/Smx.Domain/Xrf/XrfProposal.cs.
 *
 * One row of the physicist's result. The SAME shape whether it was parsed from a file or typed into
 * the manual grid, because both go through the same server-side validator — the grid is a fallback for
 * unparseable files, not a way around the checks.
 */
export interface XrfProposal {
  rowNumber: number;
  component: string;
  element: string;
  line: string;
  /** 'V' | 'L' | 'X' — X is recorded but is not a pool entry. */
  status: string;
  signalNote?: string | null;
  backgroundLevel?: number | null;
  backgroundUnit?: string | null;
  deviceModel?: string | null;
  deviceLod?: number | null;
  deviceLodUnit?: string | null;
  /** Per-row, from the parser. A row with any problem cannot be confirmed. */
  problems: string[];
}

export interface XrfParseResult {
  proposals: XrfProposal[];
  sheetProblems: string[];
}

/** GET /projects/{id}/xrf — what has already been confirmed. */
export interface XrfState {
  components: string[];
  elementPools: ElementPool[];
  measuredBackgrounds: MeasuredBackground[];
  device?: XrfDevice | null;
}

export interface XrfConfirmed {
  projectId: string;
  pools: number;
  backgrounds: number;
  device?: string | null;
}
```

- [ ] **Step 3: Add the client functions**

In `src/smx-web/src/api/client.ts`, following the existing `authorizedFetch` / `failure` / `NotFound` conventions exactly. Two written out; write `getXrfState` the same way (a `GET` returning `XrfState | NotFound`).

```ts
/**
 * Parse a physicist's result file into proposals. Writes NOTHING — the operator confirms separately,
 * which is what makes the confirmation an act rather than a consequence of choosing a file.
 */
export async function parseXrf(projectId: string, file: File): Promise<XrfParseResult> {
  const form = new FormData();
  // The field name MUST be "file" — it binds to the handler's `IFormFile file` parameter.
  form.append('file', file, file.name);

  const res = await authorizedFetch(`${BASE}/projects/${projectId}/xrf/parse`, {
    method: 'POST',
    // NO Content-Type header: the browser has to set it itself so it can append the multipart
    // boundary. Setting it by hand produces a body the server cannot parse.
    body: form,
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as XrfParseResult;
}

/** The single writer. A 422 carries the operator-readable reason the confirmation was refused. */
export async function confirmXrf(
  projectId: string,
  proposals: XrfProposal[],
): Promise<XrfConfirmed> {
  const res = await authorizedFetch(`${BASE}/projects/${projectId}/xrf/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ proposals }),
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as XrfConfirmed;
}

/**
 * What is already confirmed. A 404 means intake has not written constraints yet — a normal state for
 * a project the operator opened straight after creating it, not a failure.
 */
export async function getXrfState(projectId: string): Promise<XrfState | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/xrf`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as XrfState;
}

/** The template lives on the API, not in the bundle, so it cannot drift from the parser. */
export const xrfTemplateUrl = `${BASE}/xrf-template.csv`;
```

- [ ] **Step 4: Verify and commit**

```bash
cd /home/elimeshi/projects/repos/SMX/src/smx-web && npm install && npm run typecheck && npm test
```

Expected: typecheck clean, suite still **136** (no new tests — this task adds no behaviour).

```bash
cd /home/elimeshi/projects/repos/SMX
git add -A src/smx-web/
git commit -m "feat(web): XRF types and client, and a corrected V/X comment that had them inverted"
```

---

## Task 7: The proposals table and the confirm gate

**Files:**
- Create: `src/smx-web/src/components/xrf/XrfProposalTable.tsx`, `src/smx-web/src/components/xrf/XrfProposalTable.test.tsx`

- [ ] **Step 1: Write the failing test**

**Assert on specific elements, not on document-wide regexes.** This bit Plan 3 twice — a loose `getByText(/…/i)` matched the screen's own explanatory copy, so the test passed with the feature deleted.

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { XrfProposalTable } from './XrfProposalTable';
import type { XrfProposal } from '../../api/types';

const row = (over: Partial<XrfProposal> = {}): XrfProposal => ({
  rowNumber: 2, component: 'bottle', element: 'Ba', line: 'Ka', status: 'V',
  signalNote: null, backgroundLevel: 12.5, backgroundUnit: 'ppm',
  deviceModel: 'Niton XL5', deviceLod: 3.0, deviceLodUnit: 'ppm', problems: [], ...over,
});

const show = (proposals: XrfProposal[], onConfirm = vi.fn(), onChange = vi.fn()) =>
  render(
    <XrfProposalTable
      proposals={proposals}
      components={['bottle', 'lid']}
      onChange={onChange}
      onConfirm={onConfirm}
      busy={false}
    />,
  );

const rowFor = (element: string) => screen.getByTestId(`xrf-row-${element}`);

describe('the XRF proposals table', () => {
  it('shows every parsed row with its measurement', () => {
    show([row(), row({ element: 'Sr', rowNumber: 3, backgroundLevel: 8.1 })]);
    expect(within(rowFor('Ba')).getByText('12.5')).toBeInTheDocument();
    expect(within(rowFor('Sr')).getByText('8.1')).toBeInTheDocument();
  });

  it('marks an X row as recorded but not in the pool', () => {
    // X is a measurement, not an omission — and it must not read as a usable element. Asserted
    // inside the row, so the legend elsewhere on the screen cannot satisfy it.
    show([row({ element: 'Fe', status: 'X' })]);
    expect(within(rowFor('Fe')).getByTestId('xrf-pool-membership')).toHaveTextContent(/not in the pool/i);
  });

  /**
   * The anti-rubber-stamping rule, on screen. A conditional verdict with no stated reason cannot be
   * reviewed, only nodded at — so the button does not arm and the row says which cell is missing.
   */
  it('will not confirm while a conditional row has no signal note', () => {
    show([row({ status: 'L', signalNote: null, problems: ['a conditional (L) row must carry a signal-character note.'] })]);
    expect(screen.getByRole('button', { name: /confirm/i })).toBeDisabled();
    expect(within(rowFor('Ba')).getByTestId('xrf-row-problem')).toHaveTextContent(/signal/i);
  });

  it('arms once the note is typed', async () => {
    // The grid is EDITABLE, unlike every other analytical surface: this is the operator's
    // transcription of a human physicist's measurement, not agent output.
    const onChange = vi.fn();
    show([row({ status: 'L', signalNote: null, problems: ['needs a note'] })], vi.fn(), onChange);
    await userEvent.type(
      within(rowFor('Ba')).getByLabelText(/signal note/i), 'shoulder on Ba Ka');
    expect(onChange).toHaveBeenCalled();
  });

  it('confirms with exactly the rows on screen', async () => {
    const onConfirm = vi.fn();
    const rows = [row(), row({ element: 'Sr', rowNumber: 3 })];
    show(rows, onConfirm);
    await userEvent.click(screen.getByRole('button', { name: /confirm/i }));
    expect(onConfirm).toHaveBeenCalledWith(rows);
  });

  it('says what confirming will do, in the operator’s terms', () => {
    // Confirming is what releases Discovery. An operator who does not know that has no reason to
    // press it today rather than next week, and the project sits.
    show([row()]);
    expect(screen.getByTestId('xrf-confirm-effect')).toHaveTextContent(/discovery/i);
  });

  it('drops a row when the operator removes it', async () => {
    const onChange = vi.fn();
    show([row(), row({ element: 'Sr', rowNumber: 3 })], vi.fn(), onChange);
    await userEvent.click(within(rowFor('Sr')).getByRole('button', { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ element: 'Ba' })]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd /home/elimeshi/projects/repos/SMX/src/smx-web && npx vitest run src/components/xrf/XrfProposalTable.test.tsx
```

Expected: FAIL — `./XrfProposalTable` does not exist.

- [ ] **Step 3: Implement it**

Props: `{ proposals, components, onChange, onConfirm, busy }`. The two pieces where being subtly wrong is invisible, written out:

```tsx
/**
 * THIS GRID IS EDITABLE, AND THAT IS NOT AN INCONSISTENCY.
 *
 * Every other analytical surface in this app is read-only because the operator must not hand-edit
 * AGENT OUTPUT (CLAUDE.md Law 4) — a silent edit to an agent's conclusion captures no reason and
 * teaches the system nothing. This is the opposite case: these are the operator's transcription of a
 * HUMAN PHYSICIST's measurement, which no agent produced and no agent may alter (IntakeAnswers refuses
 * element pools by name). It is editable because of the same principle that makes the intake brief
 * read-only.
 *
 * Do not "fix" the inconsistency by making it read-only. That would delete the only entry point for
 * physics data for the second time — the creation form was the first.
 */

/**
 * Confirm arms only when every row is clean. This mirrors XrfConfirmation.Build and is a CONVENIENCE:
 * the server re-checks all of it and its refusal is the one that counts. Erring toward disabled is
 * deliberate — a button that arms and then fails is worse than one that explains why it will not.
 */
const blocked = proposals.length === 0 || proposals.some((p) => p.problems.length > 0);

/** V and L are the pool; X is measured and rejected, and must not read as usable. */
const inPool = (status: string) => status === 'V' || status === 'L';
```

The rest, all pinned by the tests above:

- One `<tr data-testid={`xrf-row-${element}`}>` per proposal: component, element, line, status, signal note, background, LOD, a **remove** button, and a `data-testid="xrf-row-problem"` cell when `problems.length > 0`.
- A `data-testid="xrf-pool-membership"` cell per row reading *"in the pool"* for V/L and *"recorded — not in the pool"* for X.
- **Editable cells.** `component` and `status` are `<select>`s (components from the prop; statuses `V`/`L`/`X`); everything else is an `<input>`. Each carries an `aria-label` — the signal-note input's must contain "signal note", which is what the test targets.
- **Put the Law 4 comment at the top of the file.** Verbatim reasoning: *this grid is editable because it is the operator's transcription of a human physicist's measurement, which no agent produced and no agent may alter — the same principle that makes the intake brief read-only.* Without it, someone will make this read-only for consistency and delete the only entry point for physics data a second time.
- **Confirm button** disabled when `busy`, when there are no proposals, or when any row has a problem. Beside it, a `data-testid="xrf-confirm-effect"` line saying confirming records the measurement and **starts Discovery**.
- The client-side problem check is a **convenience** — `XrfConfirmation.Build` re-checks everything and its refusal is the one that counts. Say so in a comment.

- [ ] **Step 4: Verify the test discriminates, then commit**

Delete the `disabled` condition on the confirm button and re-run: the conditional-note test must fail. Restore it.

```bash
npx vitest run src/components/xrf/XrfProposalTable.test.tsx && npm run typecheck && npm run build && npm test
```

Expected: 7 new tests; suite at **143**.

```bash
cd /home/elimeshi/projects/repos/SMX
git add -A src/smx-web/
git commit -m "feat(web): the XRF proposals table, with the conditional-note gate on screen"
```

---

## Task 8: Upload, manual grid, and the Background screen

**Files:**
- Create: `src/smx-web/src/components/xrf/XrfEntry.tsx`, `src/smx-web/src/components/xrf/XrfEntry.test.tsx`
- Modify: `src/smx-web/src/routes/stages/Background.tsx`

- [ ] **Step 1: Write the failing test**

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { XrfEntry } from './XrfEntry';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getXrfState: vi.fn(),
  parseXrf: vi.fn(),
  confirmXrf: vi.fn(),
  xrfTemplateUrl: '/api/xrf-template.csv',
}));
import * as api from '../../api/client';

const EMPTY = { components: ['bottle', 'lid'], elementPools: [], measuredBackgrounds: [], device: null };

beforeEach(() => {
  vi.mocked(api.getXrfState).mockResolvedValue(EMPTY);
});

const show = (onConfirmed = vi.fn()) =>
  render(<XrfEntry projectId="proj-1" onConfirmed={onConfirmed} />);

describe('XRF entry', () => {
  it('offers the template, because the parser only reads one column shape', async () => {
    // A parser this strict without a template to match it is a parser that rejects every real file.
    show();
    const link = await screen.findByRole('link', { name: /template/i });
    expect(link).toHaveAttribute('href', '/api/xrf-template.csv');
  });

  it('shows what is already confirmed, so a re-entry is visibly a re-measure', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue({
      ...EMPTY,
      elementPools: [{ component: 'bottle', element: 'Ba', line: 'Ka', status: 'V' }],
    });
    show();
    expect(await screen.findByTestId('xrf-confirmed-summary')).toHaveTextContent(/Ba/);
  });

  it('renders parsed rows for confirmation after an upload', async () => {
    vi.mocked(api.parseXrf).mockResolvedValue({
      proposals: [{
        rowNumber: 2, component: 'bottle', element: 'Ba', line: 'Ka', status: 'V',
        signalNote: null, backgroundLevel: 12.5, backgroundUnit: 'ppm',
        deviceModel: 'Niton XL5', deviceLod: 3, deviceLodUnit: 'ppm', problems: [],
      }],
      sheetProblems: [],
    });
    show();

    await userEvent.upload(
      await screen.findByLabelText(/upload/i),
      new File(['component\n'], 'result.csv', { type: 'text/csv' }));

    await waitFor(() => expect(screen.getByTestId('xrf-row-Ba')).toBeInTheDocument());
  });

  it('shows a rejected file as a stated fact, not as an empty table', async () => {
    // §5.2's discipline applied here: silence after an upload reads as "it worked".
    vi.mocked(api.parseXrf).mockRejectedValue(new Error('the file is missing these columns: status'));
    show();

    await userEvent.upload(
      await screen.findByLabelText(/upload/i),
      new File(['x'], 'wrong.csv', { type: 'text/csv' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/missing these columns/);
  });

  it('lets the operator start a row by hand when nothing parses', async () => {
    show();
    await userEvent.click(await screen.findByRole('button', { name: /enter.*by hand|manually/i }));
    await waitFor(() => expect(screen.getByTestId('xrf-manual-grid')).toBeInTheDocument());
  });

  it('surfaces the server’s refusal verbatim when confirm is rejected', async () => {
    // The client-side check is a convenience; the server's refusal is the contract, and paraphrasing
    // it would hide which row and which rule.
    vi.mocked(api.parseXrf).mockResolvedValue({
      proposals: [{
        rowNumber: 2, component: 'bottle', element: 'Ba', line: 'Ka', status: 'V',
        signalNote: null, backgroundLevel: 12.5, backgroundUnit: 'ppm',
        deviceModel: 'Niton XL5', deviceLod: 3, deviceLodUnit: 'ppm', problems: [],
      }],
      sheetProblems: [],
    });
    vi.mocked(api.confirmXrf).mockRejectedValue(new Error('row 2 measures component \'sleeve\''));
    show();

    await userEvent.upload(
      await screen.findByLabelText(/upload/i),
      new File(['component\n'], 'result.csv', { type: 'text/csv' }));
    await waitFor(() => expect(screen.getByTestId('xrf-row-Ba')).toBeInTheDocument());
    await userEvent.click(screen.getByRole('button', { name: /confirm/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/sleeve/);
  });
});
```

Note: `vi.mock` replaces the **whole module**, so anything else `XrfEntry.tsx` imports from `api/client` must appear in the factory or it is `undefined` at runtime and the failure reads as an unrelated `TypeError`.

- [ ] **Step 2: Run to verify it fails**

```bash
npx vitest run src/components/xrf/XrfEntry.test.tsx
```

Expected: FAIL — `./XrfEntry` does not exist.

- [ ] **Step 3: Implement `XrfEntry.tsx`**

Props `{ projectId, onConfirmed }`. It owns the state and the calls; `XrfProposalTable` stays presentational.

- On mount, `getXrfState(projectId)`. Render a `data-testid="xrf-confirmed-summary"` block when `elementPools.length > 0`, listing the confirmed elements per component and the device — so a second entry is visibly a **re-measure**, not a first one.
- A file input labelled so `getByLabelText(/upload/i)` finds it, and a `<a href={xrfTemplateUrl} download>` link whose accessible name contains "template".
- On upload: `parseXrf` → hold `proposals` in state → render `<XrfProposalTable>`. On rejection, render the message in a `role="alert"`.
- A **"Enter by hand"** button revealing a `data-testid="xrf-manual-grid"` wrapper around the *same* `XrfProposalTable`. Reuse the table rather than writing a second grid: **one editing surface means one set of rules**, and a second grid is where the signal-note gate would quietly not be.

```tsx
/**
 * A blank row for the manual grid.
 *
 * `problems: []` — an empty row is not yet WRONG, it is just empty, and pre-loading it with complaints
 * about cells the operator has not reached teaches them to ignore the problem column. The server
 * validates on confirm, and blank component/element/line fail there with a message naming the row.
 *
 * `rowNumber` keeps counting from whatever is already on screen so a server refusal that says "row 4"
 * points at the fourth row the operator can see.
 */
const blankRow = (rows: XrfProposal[], components: string[]): XrfProposal => ({
  rowNumber: rows.length + 1,
  component: components[0] ?? '',
  element: '',
  line: '',
  status: 'V',
  signalNote: null,
  backgroundLevel: null,
  backgroundUnit: 'ppm',
  deviceModel: rows[0]?.deviceModel ?? null,
  deviceLod: null,
  deviceLodUnit: 'ppm',
  problems: [],
});
```
- `onConfirm` → `confirmXrf(projectId, proposals)` → on success clear proposals, re-fetch the state, and call `onConfirmed()`; on rejection put the server's message **verbatim** in the `role="alert"`.

- [ ] **Step 4: Wire it into `Background.tsx`**

- Render `<XrfEntry projectId={project.projectId} onConfirmed={onRefresh ?? (() => {})} />` **above** the existing verdict matrix. `Background` currently takes no props — give it the `{ project, onRefresh }` signature `ProjectLayout` already passes every stage screen (`Intake.tsx` is the working example).
- **Keep the `MockBadge`** on the matrix. It is still fixture data. Move it so it clearly labels the *matrix*, not the whole screen, and extend its `note` to say the XRF entry above it is real.
- **Replace the `<ParkSlot awaiting="physics XRF measurement" …>`.** Its text — *"No endpoint reports a park state; the record knows only pending / running / failed / needs-review / done"* — becomes false with Task 5: Discovery now genuinely parks and its `error` says why. Show the real Discovery stage status instead, or drop the slot. Do not leave a component asserting no endpoint reports something that now does.

- [ ] **Step 5: Verify and commit**

```bash
npx vitest run src/components/xrf/ && npm run typecheck && npm run build && npm test
```

Expected: 6 new tests; suite at **149**.

```bash
cd /home/elimeshi/projects/repos/SMX
grep -rn "MockBadge" src/smx-web/src/routes/stages/Background.tsx   # must still be there
git add -A src/smx-web/
git commit -m "feat(web): XRF upload, manual grid, and Background's real entry surface"
```

---

## Task 9: Full verification

- [ ] **Step 1: Everything builds**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet build src/Smx.Backend.sln
dotnet build src/Smx.Functions.sln
cd src/smx-web && npm run build
```

Expected: **0 warnings** from both solutions; `npm run build` succeeds.

- [ ] **Step 2: Every test**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet test src/Smx.Backend.sln
dotnet test src/Smx.Functions.sln
cd src/smx-web && npm test
```

Expected: `Smx.Backend.sln` **860+** (from 816), `Smx.Functions.sln` 177 unchanged, `smx-web` **149+** (from 136). A count *below* baseline means a test was deleted — find out which and why.

- [ ] **Step 3: Confirm the properties that matter, by name**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Parse_RefusesAUnitThatIsNotPpm_RatherThanConvertingIt"
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Build_RefusesTwoMeasurementsOfTheSameElementOnTheSameComponent"
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Build_RefusesAConditionalRowWithNoSignalNote"
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Discovery_ParksWithoutCallingTheAgent_WhenThereAreNoElementPools"
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Confirm_ReplacesAnEarlierConfirmation_RatherThanAppendingToIt"
cd src/smx-web && npx vitest run -t "will not confirm while a conditional row has no signal note"
```

Expected: each passes. These six are the plan — units are refused rather than converted, a duplicate measurement is refused rather than resolved, a conditional verdict carries its reason, Discovery waits instead of failing, a re-measure corrects rather than accumulates, and the operator sees the note rule before the server has to state it.

- [ ] **Step 4: The mock badge is still on the matrix**

```bash
cd /home/elimeshi/projects/repos/SMX && grep -rn "MockBadge" src/smx-web/src/routes/ | sort
```

Expected: still present on `Background.tsx`, `Cost.tsx`, `Decision.tsx`, `Discovery.tsx`, `Dosing.tsx`; still absent from `stages/Intake.tsx`. The verdict matrix on Background is still fixture data and must still say so.

- [ ] **Step 5: The park's message reaches the operator**

```bash
cd /home/elimeshi/projects/repos/SMX && grep -rn "waiting on the physicist" src/
```

Expected: exactly one occurrence, in `StageDispatcher.cs`. The frontend renders `stage.error` verbatim (`StageStatusCard`), so the message must not be duplicated in the UI — a paraphrase that drifts is worse than one source.

- [ ] **Step 6: The tree is clean**

```bash
git status --short
```

---

## What Plan 4 deliberately does not do

- **No arbitrary vendor exports.** v1 parses one defined column shape and hands out the template for it. A parser that silently mis-maps a column is worse than one that refuses, and "understanding" a vendor's layout is exactly the job a model would do badly and invisibly.
- **No unit conversion.** A background in counts is refused, never converted: the conversion needs a calibration this system does not have, and a wrong one is invisible until deployment.
- **No real verdict matrix.** The V/L/X grid on Background is still fixture data behind its `MockBadge`. Deriving it from the confirmed pools is a genuine follow-on — the pools carry status per (component, element), but not the element-gate lock reasons or the per-cell application flags the mocked matrix shows.
- **No re-run of a Discovery that already succeeded.** Confirming pools after Discovery has produced candidates does not re-dispatch it — `OnConstraintsAsync` returns early when candidates exist. That guard is right, but it means a *corrected* background after a successful Discovery needs the existing revise path, which is out of scope here.
- **No XRF entry for a project still in interview.** The physics arrives days later by design; the entry point is the project's Background stage, not the interview.
