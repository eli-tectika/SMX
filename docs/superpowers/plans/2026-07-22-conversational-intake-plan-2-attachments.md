# Conversational Intake — Plan 2: Attachments and Extraction

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The operator drops a file into the interview and the agent can actually read it — or is told, by name and type, that it cannot, and asks. Extraction is code, before any agent sees the bytes.

**Architecture:** An upload endpoint on the backend writes the file to the existing ADLS `bronze` container at `intake/{sessionId}/{fileId}/{filename}`, runs a **synchronous, server-side** `ITextExtractor` over it, and writes the extracted text as a sibling blob. The `SessionAttachment` records `extracted` / `unsupported` / `failed`. The orchestrator's `read_attachment` tool serves that text back to the agent in pages, resolving the file **through the session document**, never by building a path from a model-supplied id.

**Tech Stack:** .NET 8 (`Smx.Backend.Tests` is `net10.0`), xUnit, Azure Storage (ADLS Gen2 / blob), PdfPig, ClosedXML, DocumentFormat.OpenXml, ASP.NET minimal APIs, Bicep.

---

## Read this before you touch anything

- **The design:** [`docs/superpowers/specs/2026-07-21-conversational-intake-design.md`](../specs/2026-07-21-conversational-intake-design.md) §5 (attachments and extraction) is what this plan implements. §5.1 (server-side and model-agnostic), §5.2 (an unreadable file is a visible fact), §5.3 (storage) are the load-bearing parts.
- **Plan 1** — [`2026-07-21-conversational-intake-plan-1-session-and-agent.md`](2026-07-21-conversational-intake-plan-1-session-and-agent.md), complete. It left `SessionAttachment`, `AttachmentStatus` and `InterviewAgent.RenderAttachments` in place with **nothing able to populate them**. This plan makes them live.
- **Why extraction is not the model's job.** The operator's instruction was explicit: *"We can't rely on Claude's file read capabilities, as it's not sure that Claude will be the used model."* Native document input would couple a data-ingestion decision to a model choice that is not fixed. Every extractor here is deterministic code, and OCR/vision arrive later behind the same interface with no schema change and no agent change.
- **Correctness is the primary design driver.** The headline harm is a **false pass**. An attachment the system silently failed to read is exactly that: the agent proceeds as though the file said nothing, and nobody is ever asked what was in it. That is why `unsupported` and `failed` are *recorded states that reach the agent's context*, not log lines.

**Baseline:** `dotnet test src/Smx.Backend.sln` is **765 passing**; `src/Smx.Functions.sln` is **177 passing**. Both solutions build with **0 warnings**. Write these numbers at the top of your working notes. Every task below adds tests; none may remove one.

### Six traps this codebase has already sprung. Do not spring them again.

1. **`AIFunctionFactory` schemas can lie.** A parameter without a default is emitted as `"required"` no matter what the description says. **Test every agent tool by invoking the real `AIFunction` via `InvokeAsync`**, never the C# method. This is why `read_attachment`'s `page` parameter has `= 1` in Task 7.
2. **`[FromServices]` is mandatory on every store parameter in a minimal-API handler.** Minimal APIs resolve service-vs-body via `IServiceProviderIsService` at endpoint-build time across the **whole app's** endpoint data source. Miss it and routing breaks for **every** route, `/healthz` included.
3. **PdfPig's `page.Text` flattens a page into a single line.** Use `ContentOrderTextExtractor`. This is not hypothetical — it blinded a line-anchored regex and rejected every real SDS in production (2026-07-16). See the comment in `src/Smx.Functions/Sds/Ingestion/PdfTextExtractor.cs`. The same reasoning applies to `.docx`: `body.InnerText` concatenates every paragraph into one run-on line.
4. **.NET 8 minimal APIs reject multipart form posts with a 400 unless the endpoint calls `.DisableAntiforgery()`.** The failure looks like a malformed request, not a missing configuration.
5. **Azure/Storage failures are silent.** A missing container 404s and looks like empty data. Assume nothing succeeded unless you checked.
6. **`Smx.Backend.Tests` targets `net10.0`; every other project is `net8.0` with `RollForward=Major`.** The net8 `TestHost` is incompatible with the STJ that ships in the only-installed net10 runtime. Do not "fix" this, and put ASP.NET host tests in `Smx.Backend.Tests`.
7. **The build is at 0 warnings and every task below must keep it there.** The likeliest new warning in this plan is `CS8604` from asserting on a nullable — `ExtractionResult.Error` and `SessionAttachment.TextBlobPath` are both `string?`. Add a `!` or an `Assert.NotNull` first; do not suppress the warning, and do not let one accumulate "just for now".

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Intake/ITextExtractor.cs` | The port + `ExtractionResult`. Pure. |
| `src/Smx.Domain/Intake/AttachmentLimits.cs` | The numbers, with their reasons. Pure. |
| `src/Smx.Domain/Intake/AttachmentPaths.cs` | Blob path construction + filename sanitisation. Pure. |
| `src/Smx.Domain/IAttachmentBlobStore.cs` | The blob port (backend writes, orchestrator reads) |
| `src/Smx.Infrastructure/BlobAttachmentStore.cs` | Its ADLS adapter |
| `src/Smx.Backend/Extraction/PlainTextExtractor.cs` | `.txt` `.md` `.json` `.xml` `.csv` `.tsv` |
| `src/Smx.Backend/Extraction/XlsxExtractor.cs` | `.xlsx` via ClosedXML |
| `src/Smx.Backend/Extraction/DocxExtractor.cs` | `.docx` via DocumentFormat.OpenXml |
| `src/Smx.Backend/Extraction/PdfExtractor.cs` | `.pdf` text layer via PdfPig |
| `src/Smx.Backend/Extraction/TextExtraction.cs` | Picks an extractor; never throws |
| `src/Smx.Backend/Api/AttachmentEndpoints.cs` | The upload surface |
| `src/Smx.Domain.Tests/Fakes/InMemoryAttachmentBlobStore.cs` | Shared by source-link |
| `src/Smx.Domain.Tests/AttachmentPathsTests.cs` | |
| `src/Smx.Backend.Tests/ExtractorTests.cs` · `AttachmentEndpointsTests.cs` | |
| `src/Smx.Orchestrator.Tests/ReadAttachmentTests.cs` | |

**Modify:** `Smx.Backend/Smx.Backend.csproj` · `Smx.Backend/Program.cs` · `Smx.Orchestrator/Program.cs` · `Smx.Orchestrator/Agents/InterviewTools.cs` · `Smx.Orchestrator/Agents/InterviewAgent.cs` · `Smx.Domain.Tests/Fakes/*` · `Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj` · `Smx.Backend.Tests/Smx.Backend.Tests.csproj` · `infra/modules/compute.bicep` · `infra/main.bicep` · `infra/single-rg/modules/compute.bicep` · `infra/single-rg/main.bicep`

**Why the extractors live in `Smx.Backend` and not `Smx.Infrastructure`:** only the backend extracts. The orchestrator's `read_attachment` reads an already-extracted `.txt` blob and never opens a PDF. Putting the parsers behind the shared infrastructure boundary would hand three parsing dependencies to a process that has no use for them.

---

## Task 1: The extraction port, the limits, and the paths

Pure domain. No I/O, no packages.

**Files:**
- Create: `src/Smx.Domain/Intake/ITextExtractor.cs`, `AttachmentLimits.cs`, `AttachmentPaths.cs`
- Test: `src/Smx.Domain.Tests/AttachmentPathsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Intake;
using Xunit;

namespace Smx.Domain.Tests;

public class AttachmentPathsTests
{
    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system.ini", "system.ini")]
    [InlineData("/absolute/path/report.pdf", "report.pdf")]
    [InlineData("normal name (1).pdf", "normal_name_1.pdf")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    public void SafeFilename_StripsEverythingThatCouldLeaveTheFolder(string input, string expected) =>
        Assert.Equal(expected, AttachmentPaths.SafeFilename(input));

    [Fact]
    public void Blob_PutsTheFileUnderItsOwnSessionAndFileId()
    {
        // The fileId segment is what keeps two uploads of "report.pdf" in one session from colliding.
        var path = AttachmentPaths.Blob("isx-aaaa1111", "att-bbbb2222", "report.pdf");
        Assert.Equal("intake/isx-aaaa1111/att-bbbb2222/report.pdf", path);
    }

    [Fact]
    public void Blob_CannotEscapeTheSessionFolder_EvenWithATraversingFilename()
    {
        // The filename arrives from a browser and is attacker-controlled in the general case. A path
        // that climbs out of intake/{sessionId}/ would let one session's upload overwrite another's.
        var path = AttachmentPaths.Blob("isx-aaaa1111", "att-bbbb2222", "../../other/evil.pdf");
        Assert.StartsWith("intake/isx-aaaa1111/att-bbbb2222/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IsASiblingOfTheFile_AndDoesNotDependOnTheFilename()
    {
        // Fixed name: the extracted text must be findable from (sessionId, fileId) alone, without
        // knowing what the original file was called.
        Assert.Equal("intake/isx-aaaa1111/att-bbbb2222/extracted.txt",
            AttachmentPaths.Text("isx-aaaa1111", "att-bbbb2222"));
    }

    [Theory]
    [InlineData("report.PDF", ".pdf")]
    [InlineData("data.tar.gz", ".gz")]
    [InlineData("noextension", "")]
    public void Extension_IsLowercasedAndTakenFromTheSanitisedName(string filename, string expected) =>
        Assert.Equal(expected, AttachmentPaths.Extension(filename));
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter AttachmentPathsTests
```

Expected: FAIL — `AttachmentPaths` does not exist.

- [ ] **Step 3: Implement `src/Smx.Domain/Intake/AttachmentPaths.cs`**

```csharp
using System.Text;

namespace Smx.Domain.Intake;

/// Where an attachment's bytes and its extracted text live, and how a browser-supplied filename is made
/// safe to put in a path.
///
/// Sanitisation is not decorative. The filename arrives from the operator's machine, and a path that
/// climbs out of `intake/{sessionId}/{fileId}/` would let one interview's upload land on top of
/// another's — or somewhere else in the `bronze` container entirely, which also holds the SDS corpus.
public static class AttachmentPaths
{
    public static string Blob(string sessionId, string fileId, string filename) =>
        $"intake/{sessionId}/{fileId}/{SafeFilename(filename)}";

    /// A FIXED name, deliberately independent of the original filename: the reader
    /// (`read_attachment`, in the orchestrator) resolves text from (sessionId, fileId) and should not
    /// have to know, or trust, what the file was called.
    public static string Text(string sessionId, string fileId) =>
        $"intake/{sessionId}/{fileId}/extracted.txt";

    /// Allowlist, not denylist: everything outside [A-Za-z0-9._-] becomes '_', and any directory part
    /// is dropped first. A denylist of "../" and friends is the version that gets bypassed.
    public static string SafeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "file";

        // Drop directory components under BOTH separators — the upload may come from Windows.
        var name = filename.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');

        // Collapse runs of '_' so "normal name (1).pdf" does not become "normal_name__1_.pdf".
        var collapsed = new StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
            if (c != '_' || collapsed.Length == 0 || collapsed[^1] != '_') collapsed.Append(c);

        var result = collapsed.ToString().Trim('_', '.');
        // "..", "." and "" all sanitise to nothing; a blank final segment would make the path a folder.
        if (result.Length == 0) return "file";
        return result.Length > 120 ? result[..120] : result;
    }

    public static string Extension(string filename)
    {
        var ext = Path.GetExtension(SafeFilename(filename));
        return string.IsNullOrEmpty(ext) ? "" : ext.ToLowerInvariant();
    }
}
```

> Note the `Trim('_', '.')`: `"normal name (1).pdf"` sanitises to `normal_name_1_.pdf` before the trim, and the test expects `normal_name_1.pdf`. Verify the theory cases actually pass rather than assuming — if a case disagrees, fix the CODE to satisfy the stated intent (a safe, readable name), not the test.

- [ ] **Step 4: Implement `src/Smx.Domain/Intake/AttachmentLimits.cs`**

```csharp
namespace Smx.Domain.Intake;

/// The numbers, in one place, with the reason each one exists. They are enforced SERVER-SIDE: a browser
/// check is a courtesy, not a limit.
public static class AttachmentLimits
{
    /// 25 MB. Big enough for a scanned 100-page questionnaire, small enough that extraction stays
    /// synchronous inside one HTTP request without a queue.
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// 20 files per session. A cap on the whole interview, not per upload — without it a session
    /// document grows unbounded and eventually exceeds Cosmos's 2 MB item limit on its metadata alone.
    public const int MaxFilesPerSession = 20;

    /// Extracted text is truncated here before it is stored. A .docx or .xlsx is a ZIP archive and can
    /// expand enormously from a small upload; without a ceiling a 25 MB workbook can produce hundreds of
    /// megabytes of text and take the backend's memory with it.
    public const int MaxExtractedChars = 400_000;

    /// One `read_attachment` page. Sized so a long document enters the prompt deliberately, a page at a
    /// time, rather than all at once — the agent asks for more if it needs more.
    public const int PageChars = 6_000;
}
```

- [ ] **Step 5: Implement `src/Smx.Domain/Intake/ITextExtractor.cs`**

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Intake;

/// <param name="Text">The extracted text. Empty unless Status is `extracted`.</param>
/// <param name="Status">One of <see cref="AttachmentStatus"/>.</param>
/// <param name="Error">Why it could not be read. Shown to the OPERATOR and put in the AGENT's context,
/// so it must read as a sentence a person can act on, not a stack trace.</param>
public sealed record ExtractionResult(string Text, string Status, string? Error = null)
{
    public static ExtractionResult Extracted(string text) =>
        new(text, AttachmentStatus.Extracted);

    public static ExtractionResult Unsupported(string what) =>
        new("", AttachmentStatus.Unsupported, $"there is no extractor for {what}");

    public static ExtractionResult Failed(string why) =>
        new("", AttachmentStatus.Failed, why);
}

/// Turns one uploaded file into text, in CODE, before any agent sees it.
///
/// Deliberately model-agnostic: relying on a model's native document or vision input would couple a
/// data-ingestion decision to the choice of model, and the model is not fixed (design §5.1). OCR,
/// image and scanned-PDF extractors arrive later behind this same interface — no schema change, no
/// agent change.
public interface ITextExtractor
{
    /// `extension` is lowercased and includes the dot (".pdf"). `contentType` is whatever the browser
    /// claimed and is ADVISORY ONLY — browsers send `application/octet-stream` for perfectly ordinary
    /// files, so an implementation that requires a content-type match will refuse real uploads.
    bool CanHandle(string contentType, string extension);

    Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct);
}
```

- [ ] **Step 6: Run to verify it passes**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter AttachmentPathsTests
```

Expected: PASS, 13 tests (6 + 1 + 1 + 1 + 3 theory cases + 1).

- [ ] **Step 7: Run the whole suite and commit**

```bash
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): the extraction port, the limits, and a filename that cannot escape its folder"
```

Expected: 765 + 13 = 778, zero failures.

---

## Task 2: The plain-text extractor

**Files:**
- Create: `src/Smx.Backend/Extraction/PlainTextExtractor.cs`
- Test: `src/Smx.Backend.Tests/ExtractorTests.cs`

**On `.csv`/`.tsv`:** design §5.1 lists them separately from `.txt`/`.md`/`.json`/`.xml`, but the extraction is the same operation — read the bytes as text. A CSV-aware parser would add nothing the agent cannot already read and would risk mangling quoted fields containing newlines. One extractor handles both groups, and this paragraph is the reason.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using Smx.Backend.Extraction;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class ExtractorTests
{
    private static Stream Bytes(byte[] b) => new MemoryStream(b);
    private static Stream Utf8(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".json")]
    [InlineData(".xml")]
    [InlineData(".csv")]
    [InlineData(".tsv")]
    public void PlainText_HandlesEveryTextExtension(string ext) =>
        Assert.True(new PlainTextExtractor().CanHandle("application/octet-stream", ext));

    [Fact]
    public void PlainText_DoesNotClaimBinaryFormats()
    {
        var x = new PlainTextExtractor();
        Assert.False(x.CanHandle("application/pdf", ".pdf"));
        Assert.False(x.CanHandle("image/jpeg", ".jpg"));
    }

    [Fact]
    public async Task PlainText_ReadsTheContentAndKeepsItsLines()
    {
        var result = await new PlainTextExtractor().ExtractAsync(Utf8("line one\nline two"), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.Contains("line one", result.Text, StringComparison.Ordinal);
        Assert.Contains("line two", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainText_StripsAUtf8Bom_SoTheFirstFieldNameIsNotCorrupted()
    {
        // A CSV saved by Excel starts with a BOM. Left in place it becomes an invisible prefix on the
        // first header, and every downstream comparison against that header silently stops matching.
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("cas,name")).ToArray();

        var result = await new PlainTextExtractor().ExtractAsync(Bytes(withBom), default);

        Assert.StartsWith("cas", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainText_TruncatesAtTheCeiling_AndSaysItDidSo()
    {
        // Silent truncation reads as "that was the whole file" — which is the false-pass shape this
        // system exists to avoid. The marker is what lets the agent know to ask.
        var huge = new string('x', AttachmentLimits.MaxExtractedChars + 5_000);

        var result = await new PlainTextExtractor().ExtractAsync(Utf8(huge), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.True(result.Text.Length <= AttachmentLimits.MaxExtractedChars + 200,
            $"text was {result.Text.Length} chars — the ceiling was not applied");
        Assert.Contains("truncated", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ExtractorTests
```

Expected: FAIL — `PlainTextExtractor` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text;
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// Everything that is already text. `.csv`/`.tsv` are here rather than in a delimiter-aware extractor
/// on purpose: parsing them into cells and re-serialising would add nothing the agent cannot read, and
/// would risk mangling quoted fields that contain commas or newlines.
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".json", ".xml", ".csv", ".tsv" };

    public bool CanHandle(string contentType, string extension) => Extensions.Contains(extension);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        // detectEncodingFromByteOrderMarks: a CSV saved by Excel carries a UTF-8 BOM, which would
        // otherwise survive as an invisible prefix on the first header and quietly break every
        // comparison against it. UTF-8 is the fallback when there is no BOM.
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        return ExtractionResult.Extracted(Truncate(text));
    }

    /// Shared by every extractor in this folder. Truncation is ANNOUNCED, never silent: a cut-off
    /// document that looks complete is a document the agent will confidently reason about the end of.
    internal static string Truncate(string text) =>
        text.Length <= AttachmentLimits.MaxExtractedChars
            ? text
            : text[..AttachmentLimits.MaxExtractedChars] +
              $"\n\n[... truncated at {AttachmentLimits.MaxExtractedChars} characters — this file is longer " +
              "than shown. Ask the operator about anything you need from the rest of it.]";
}
```

- [ ] **Step 4: Run to verify it passes, then commit**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ExtractorTests
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): the plain-text extractor, with announced truncation"
```

Expected: 10 new tests (6 theory + 4), suite at 788.

---

## Task 3: The PDF, .docx and .xlsx extractors

**Files:**
- Modify: `src/Smx.Backend/Smx.Backend.csproj` (add `PdfPig`, `DocumentFormat.OpenXml`)
- Modify: `src/Smx.Backend.Tests/Smx.Backend.Tests.csproj` (link the existing PDF fixture)
- Create: `src/Smx.Backend/Extraction/PdfExtractor.cs`, `DocxExtractor.cs`, `XlsxExtractor.cs`
- Test: `src/Smx.Backend.Tests/ExtractorTests.cs` (add to it)

- [ ] **Step 1: Add the packages and the fixture link**

In `src/Smx.Backend/Smx.Backend.csproj`, beside the existing `ClosedXML` reference:

```xml
    <!-- Same version as Smx.Functions, which already uses PdfPig for the SDS text layer. -->
    <PackageReference Include="PdfPig" Version="0.1.15" />
    <!-- .docx. ClosedXML already pulls this in transitively; referenced EXPLICITLY because relying on a
         transitive dependency means a ClosedXML upgrade can silently remove it.
         3.1.1 is what ClosedXML 0.104.2 actually resolves. Do not write a literal double dash in an
         XML comment when documenting the command that proves it: it is illegal XML and MSB4025s. -->
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.1.1" />
```

Verify the `DocumentFormat.OpenXml` version ClosedXML 0.104.2 actually resolves, and match it:

```bash
dotnet list src/Smx.Backend/Smx.Backend.csproj package --include-transitive | grep -i openxml
```

In `src/Smx.Backend.Tests/Smx.Backend.Tests.csproj`, reuse the PDF already committed for the SDS tests rather than adding a second binary to the repo:

```xml
  <ItemGroup>
    <!-- A real 16-section GHS SDS, already committed for Smx.Functions.Tests. Linked rather than
         copied: one fixture, one place to update, and no new binary in the tree. -->
    <Content Include="../Smx.Functions.Tests/Resources/real-sds-nd2o3.pdf"
             Link="Resources/real-sds-nd2o3.pdf" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

Add to `src/Smx.Backend.Tests/ExtractorTests.cs`:

```csharp
    [Fact]
    public async Task Pdf_ExtractsTheTextLayer_AndPreservesLineStructure()
    {
        // THE regression guard, inherited from a live 2026-07-16 finding in Smx.Functions: PdfPig's
        // page.Text concatenates a whole page into ONE line. Anything downstream that is line-anchored
        // then matches nothing, and the file looks empty rather than broken.
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/real-sds-nd2o3.pdf"), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.Contains("1313-97-9", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.Split('\n').Length > 20,
            "the whole document came back as a handful of lines — page.Text was used instead of " +
            "ContentOrderTextExtractor");
    }

    [Fact]
    public async Task Pdf_ReportsFailed_ForSomethingThatIsNotAPdf()
    {
        // A file named .pdf that is not one must be a RECORDED failure the agent asks about, not an
        // exception that fails the upload.
        var result = await new PdfExtractor().ExtractAsync(Utf8("this is not a pdf"), default);

        Assert.Equal(AttachmentStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Docx_ExtractsParagraphs_AsSeparateLines()
    {
        // Same lesson as the PDF: body.InnerText runs every paragraph together into one line.
        var result = await new DocxExtractor().ExtractAsync(BuildDocx("First para.", "Second para."), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains("First para.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Second para.", StringComparison.Ordinal));
        Assert.True(lines.Length >= 2, "both paragraphs came back on one line");
    }

    [Fact]
    public async Task Xlsx_ExtractsEveryWorksheet_AndNamesThem()
    {
        // A workbook's sheet names carry meaning ("Bottle", "Lid"), and a cell value is ambiguous
        // without knowing which sheet it came from.
        var result = await new XlsxExtractor().ExtractAsync(BuildXlsx(), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.Contains("Components", result.Text, StringComparison.Ordinal);   // the sheet name
        Assert.Contains("bottle", result.Text, StringComparison.Ordinal);
        Assert.Contains("PET", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xlsx_ReportsFailed_ForSomethingThatIsNotAWorkbook()
    {
        var result = await new XlsxExtractor().ExtractAsync(Utf8("not a workbook"), default);
        Assert.Equal(AttachmentStatus.Failed, result.Status);
    }

    /// Built in-test rather than committed: a generated fixture cannot drift from what the code expects,
    /// and both libraries can write the format they read.
    private static Stream BuildDocx(params string[] paragraphs)
    {
        var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                   ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    paragraphs.Select(p => new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text(p))))));
            main.Document.Save();
        }
        return new MemoryStream(ms.ToArray());
    }

    private static Stream BuildXlsx()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var sheet = wb.Worksheets.Add("Components");
        sheet.Cell(1, 1).Value = "component";
        sheet.Cell(1, 2).Value = "material";
        sheet.Cell(2, 1).Value = "bottle";
        sheet.Cell(2, 2).Value = "PET";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new MemoryStream(ms.ToArray());
    }
```

> `WordprocessingDocument.Create` and `XLWorkbook.SaveAs` both leave the stream in a state the reader may not accept, which is why each helper returns a **fresh `MemoryStream` over the finished bytes**. Do not "simplify" that by seeking the original stream to 0.

- [ ] **Step 3: Run to verify they fail**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ExtractorTests
```

Expected: FAIL — the three extractors do not exist.

- [ ] **Step 4: Implement `PdfExtractor.cs`**

```csharp
using System.Text;
using Smx.Domain.Intake;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Smx.Backend.Extraction;

/// The PDF text layer. A scanned PDF has none and comes back as a near-empty `extracted` — see the note
/// in ExtractAsync. OCR arrives later behind ITextExtractor with no change here.
public sealed class PdfExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string extension) =>
        string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);

            using var doc = PdfDocument.Open(ms.ToArray());
            var sb = new StringBuilder();
            // ContentOrderTextExtractor, NOT page.Text. page.Text concatenates a page into a single
            // line; in Smx.Functions that blinded a line-anchored regex and rejected every real SDS
            // (live 2026-07-16). Line structure is the whole value of a text layer.
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            }

            var text = sb.ToString();
            // A scanned PDF parses cleanly and yields nothing. Reporting `extracted` with empty text
            // would tell the agent the file was read and said nothing — the false-pass shape. Say the
            // truth instead, and let it ask.
            if (string.IsNullOrWhiteSpace(text))
                return ExtractionResult.Failed(
                    "this PDF has no text layer — it is probably a scan or a set of images");

            return ExtractionResult.Extracted(PlainTextExtractor.Truncate(text));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // NEVER let this escape: an unreadable file must become a recorded status the agent asks
            // about, not a 500 that loses the upload the operator just made.
            return ExtractionResult.Failed($"could not read this PDF ({e.Message})");
        }
    }
}
```

- [ ] **Step 5: Implement `DocxExtractor.cs`**

```csharp
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

public sealed class DocxExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string extension) =>
        string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);
            ms.Position = 0;

            using var doc = WordprocessingDocument.Open(ms, isEditable: false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body is null) return ExtractionResult.Failed("this .docx has no document body");

            // Paragraph by paragraph, NOT body.InnerText: InnerText runs the entire document together
            // into one line, which is the same mistake page.Text makes on a PDF. A questionnaire whose
            // question/answer structure is flattened is a questionnaire the agent misreads.
            var sb = new StringBuilder();
            foreach (var p in body.Descendants<Paragraph>())
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(p.InnerText);
            }

            var text = sb.ToString();
            return string.IsNullOrWhiteSpace(text)
                ? ExtractionResult.Failed("this .docx contains no text")
                : ExtractionResult.Extracted(PlainTextExtractor.Truncate(text));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ExtractionResult.Failed($"could not read this .docx ({e.Message})");
        }
    }
}
```

- [ ] **Step 6: Implement `XlsxExtractor.cs`**

```csharp
using System.Text;
using ClosedXML.Excel;
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// ClosedXML is already a dependency here (the compatibility-matrix export) and in
/// tools/Smx.ReferenceData.Transform, so the workbook reader is one this codebase already trusts.
public sealed class XlsxExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string extension) =>
        string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);
            ms.Position = 0;

            using var wb = new XLWorkbook(ms);
            var sb = new StringBuilder();
            foreach (var sheet in wb.Worksheets)
            {
                ct.ThrowIfCancellationRequested();
                // The sheet name is data: a workbook with a tab per component is a component breakdown,
                // and a bare cell value is ambiguous without knowing which sheet it came from.
                sb.AppendLine($"# sheet: {sheet.Name}");

                // RangeUsed() rather than the whole sheet: an empty .xlsx addresses ~1M rows, and
                // iterating them produces a gigabyte of tabs before the truncation ceiling is reached.
                if (sheet.RangeUsed() is not { } used) { sb.AppendLine("(empty)"); continue; }

                foreach (var row in used.Rows())
                {
                    // Tab-separated: it round-trips into the prompt as a readable grid, and it is what
                    // the operator would have pasted anyway.
                    sb.AppendLine(string.Join('\t', row.Cells().Select(c => c.GetFormattedString())));
                    if (sb.Length > AttachmentLimits.MaxExtractedChars) break;
                }
            }

            return ExtractionResult.Extracted(PlainTextExtractor.Truncate(sb.ToString()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ExtractionResult.Failed($"could not read this workbook ({e.Message})");
        }
    }
}
```

- [ ] **Step 7: Run to verify they pass, then commit**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ExtractorTests
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): PDF, .docx and .xlsx extractors — line structure is the point"
```

Expected: 5 new tests, suite at 793.

---

## Task 4: The dispatcher that never throws

**Files:**
- Create: `src/Smx.Backend/Extraction/TextExtraction.cs`
- Test: `src/Smx.Backend.Tests/ExtractorTests.cs` (add to it)

- [ ] **Step 1: Write the failing test**

```csharp
    private static TextExtraction AllExtractors() => new(
        [new PlainTextExtractor(), new PdfExtractor(), new DocxExtractor(), new XlsxExtractor()]);

    [Fact]
    public async Task Extraction_ReportsUnsupported_ForAFormatWithNoExtractor_AndNamesIt()
    {
        // The agent is shown this. "unsupported" alone tells the operator nothing; naming the type is
        // what turns a dead file into a question worth asking.
        var result = await AllExtractors().ExtractAsync("line-photo.jpg", "image/jpeg", Utf8("...."), default);

        Assert.Equal(AttachmentStatus.Unsupported, result.Status);
        Assert.Contains(".jpg", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extraction_PicksByExtension_NotByTheBrowsersContentType()
    {
        // Browsers routinely send application/octet-stream for ordinary files. An implementation that
        // trusted the content type would refuse most real uploads.
        var result = await AllExtractors().ExtractAsync("notes.md", "application/octet-stream",
            Utf8("# heading"), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.Contains("heading", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extraction_TurnsAThrowingExtractorIntoAFailedStatus()
    {
        // An extractor that throws must not fail the upload: the operator's file is already stored, and
        // losing the whole request would lose the attachment they just added.
        var result = await new TextExtraction([new ThrowingExtractor()])
            .ExtractAsync("x.boom", "application/octet-stream", Utf8("x"), default);

        Assert.Equal(AttachmentStatus.Failed, result.Status);
        Assert.Contains("detonated", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingExtractor : ITextExtractor
    {
        public bool CanHandle(string contentType, string extension) => extension == ".boom";
        public Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct) =>
            throw new InvalidOperationException("detonated");
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter Extraction_
```

Expected: FAIL — `TextExtraction` does not exist.

- [ ] **Step 3: Implement**

```csharp
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// Picks the extractor for a file and guarantees an ExtractionResult comes back — never an exception.
///
/// That guarantee is the point. The bytes are already in blob storage by the time this runs; letting a
/// malformed file throw would fail the request and lose the attachment the operator just added, while
/// leaving an orphaned blob behind. A recorded `failed` keeps the file visible and turns it into a
/// question the agent asks (design §5.2).
public sealed class TextExtraction(IReadOnlyList<ITextExtractor> extractors)
{
    public async Task<ExtractionResult> ExtractAsync(
        string filename, string contentType, Stream input, CancellationToken ct)
    {
        var extension = AttachmentPaths.Extension(filename);

        var extractor = extractors.FirstOrDefault(e => e.CanHandle(contentType ?? "", extension));
        if (extractor is null)
            return ExtractionResult.Unsupported(
                extension.Length > 0 ? $"{extension} files" : $"files like '{filename}'");

        try
        {
            return await extractor.ExtractAsync(input, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ExtractionResult.Failed($"could not read this file ({e.Message})");
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes, then commit**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ExtractorTests
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): the extractor dispatcher — an unreadable file is a status, not an exception"
```

Expected: 3 new tests, suite at 796.

---

## Task 5: The attachment blob store

**Files:**
- Create: `src/Smx.Domain/IAttachmentBlobStore.cs`, `src/Smx.Infrastructure/BlobAttachmentStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryAttachmentBlobStore.cs`
- Modify: `src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj`, `src/Smx.Backend.Tests/Smx.Backend.Tests.csproj` (source-link the fake)
- Test: `src/Smx.Domain.Tests/InMemoryAttachmentBlobStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using Smx.Domain;
using Smx.Domain.Tests.Fakes;
using Xunit;

namespace Smx.Domain.Tests;

public class InMemoryAttachmentBlobStoreTests
{
    [Fact]
    public async Task RoundTripsText()
    {
        var store = new InMemoryAttachmentBlobStore();
        await store.PutTextAsync("intake/s/f/extracted.txt", "hello");
        Assert.Equal("hello", await store.GetTextAsync("intake/s/f/extracted.txt"));
    }

    [Fact]
    public async Task RoundTripsBytes()
    {
        var store = new InMemoryAttachmentBlobStore();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"));
        await store.PutAsync("intake/s/f/report.pdf", content, "application/pdf");
        Assert.True(store.Exists("intake/s/f/report.pdf"));
    }

    [Fact]
    public async Task Returns_NullForAMissingBlob() =>
        Assert.Null(await new InMemoryAttachmentBlobStore().GetTextAsync("intake/nope/nope/extracted.txt"));
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter InMemoryAttachmentBlobStoreTests
```

Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the port** — `src/Smx.Domain/IAttachmentBlobStore.cs`:

```csharp
namespace Smx.Domain;

/// Interview attachment bytes and their extracted text, in the existing ADLS `bronze` container.
///
/// Both apps use this, asymmetrically: the BACKEND writes (upload + extraction) and the ORCHESTRATOR
/// only ever calls GetTextAsync (the read_attachment tool). Blobs are written once and never
/// re-parented — IntakeBriefDoc references the session-scoped path even after the project exists,
/// because copying them on creation would add a partial-failure mode (project created, half the blobs
/// moved) for no benefit: the session DOCUMENT is disposable, the bytes never were (design §5.3).
public interface IAttachmentBlobStore
{
    Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default);
    Task PutTextAsync(string path, string text, CancellationToken ct = default);
    Task<string?> GetTextAsync(string path, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the fake** — `src/Smx.Domain.Tests/Fakes/InMemoryAttachmentBlobStore.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryAttachmentBlobStore : IAttachmentBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public bool Exists(string path) => _blobs.ContainsKey(path);
    public IReadOnlyCollection<string> Paths => _blobs.Keys.ToList();

    public async Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _blobs[path] = ms.ToArray();
    }

    public Task PutTextAsync(string path, string text, CancellationToken ct = default)
    {
        _blobs[path] = Encoding.UTF8.GetBytes(text);
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null);
}
```

Source-link it into the other two test projects (a `ProjectReference` would cause CS0433 — this codebase shares fakes by source-link; copy exactly how `InMemoryIntakeSessionStore.cs` is already linked in both csproj files):

```xml
    <Compile Include="../Smx.Domain.Tests/Fakes/InMemoryAttachmentBlobStore.cs"
             Link="Fakes/InMemoryAttachmentBlobStore.cs" />
```

- [ ] **Step 5: Implement the adapter** — `src/Smx.Infrastructure/BlobAttachmentStore.cs`:

```csharp
using System.Text;
using Azure;
using Azure.Storage.Files.DataLake;
using Smx.Domain;

namespace Smx.Infrastructure;

/// The `bronze` ADLS Gen2 filesystem. DataLakeFileSystemClient rather than BlobContainerClient to match
/// AdlsBronzeStore in Smx.Functions, which already writes the SDS corpus into the same filesystem.
public sealed class BlobAttachmentStore(DataLakeFileSystemClient fs) : IAttachmentBlobStore
{
    public async Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default) =>
        await fs.GetFileClient(path).UploadAsync(content, overwrite: true, ct);

    public async Task PutTextAsync(string path, string text, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await fs.GetFileClient(path).UploadAsync(ms, overwrite: true, ct);
    }

    public async Task<string?> GetTextAsync(string path, CancellationToken ct = default)
    {
        var file = fs.GetFileClient(path);
        try
        {
            var resp = await file.ReadAsync(ct);
            using var reader = new StreamReader(resp.Value.Content, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // A missing blob is a null, not a fault: the session may predate extraction, or the file
            // may have been `unsupported` and never produced text at all.
            return null;
        }
    }
}
```

> Check `AdlsBronzeStore`'s real `ReadAsync` overload before compiling — it calls `file.ReadAsync(cancellationToken: ct)`. Match whichever overload the installed SDK version actually exposes.

- [ ] **Step 6: Run to verify it passes, then commit**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter InMemoryAttachmentBlobStoreTests
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): the attachment blob store — write once, never re-parent"
```

Expected: 3 new tests, suite at 799.

---

## Task 6: The upload endpoint

**Files:**
- Create: `src/Smx.Backend/Api/AttachmentEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/AttachmentEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

Follow the host-building shape `IntakeSessionEndpointsTests` already uses (`IClassFixture<WebApplicationFactory<Program>>` + a `NewApp(...)` helper via `WithWebHostBuilder`). Read it and match its real signature.

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class AttachmentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AttachmentEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IIntakeSessionStore sessions, IAttachmentBlobStore blobs) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(sessions);
            s.AddSingleton(blobs);
        }));

    private static async Task<(string id, InMemoryIntakeSessionStore sessions)> SeededAsync()
    {
        var sessions = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-22T10:00:00.0000000Z",
        });
        return (id, sessions);
    }

    /// Named FilePart, not File: a static member called `File` would shadow `System.IO.File` for the
    /// whole class, and the next person to add a fixture-reading test gets a baffling compile error.
    /// The form field name MUST be "file" — it is what binds to the handler's `IFormFile file`.
    private static MultipartFormDataContent FilePart(string filename, string content, string contentType)
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", filename } };
    }

    [Fact]
    public async Task Upload_StoresTheFile_ExtractsIt_AndRecordsItOnTheSession()
    {
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("notes.txt", "the adhesive is water-based", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var attachment = Assert.Single((await sessions.GetAsync(id))!.Attachments);
        Assert.Equal(AttachmentStatus.Extracted, attachment.Status);
        Assert.Equal("notes.txt", attachment.Filename);
        // Both blobs: the original bytes AND the extracted text.
        Assert.True(blobs.Exists(attachment.BlobPath));
        Assert.Equal("the adhesive is water-based", await blobs.GetTextAsync(attachment.TextBlobPath!));
    }

    [Fact]
    public async Task Upload_RecordsAnUnreadableFile_RatherThanRejectingIt()
    {
        // THE point of design §5.2. A file we cannot parse must still be stored and still appear in the
        // agent's context by name and status, so the agent asks the operator what it shows. Refusing
        // the upload would make the file — and the fact that it exists — invisible.
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("line-photo.jpg", "not text, and no extractor claims .jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var attachment = Assert.Single((await sessions.GetAsync(id))!.Attachments);
        Assert.Equal(AttachmentStatus.Unsupported, attachment.Status);
        Assert.False(string.IsNullOrWhiteSpace(attachment.Error));
        Assert.True(blobs.Exists(attachment.BlobPath), "the bytes must be kept even when unreadable");
        Assert.Null(attachment.TextBlobPath);
    }

    [Fact]
    public async Task Upload_IsA404ForAnUnknownSession()
    {
        using var app = NewApp(new InMemoryIntakeSessionStore(), new InMemoryAttachmentBlobStore());
        var res = await app.CreateClient().PostAsync("/intake-sessions/isx-nope/attachments",
            FilePart("notes.txt", "hi", "text/plain"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Upload_RefusesOnceTheSessionIsFull()
    {
        // Server-side, not a browser courtesy: the cap exists so the session document's attachment
        // metadata cannot grow past Cosmos's 2 MB item limit.
        var (id, sessions) = await SeededAsync();
        var session = (await sessions.GetAsync(id))!;
        for (var i = 0; i < AttachmentLimits.MaxFilesPerSession; i++)
            session.Attachments.Add(new SessionAttachment
            {
                FileId = $"att-{i:D4}", Filename = $"f{i}.txt", Status = AttachmentStatus.Extracted,
            });
        await sessions.UpsertAsync(session);
        using var app = NewApp(sessions, new InMemoryAttachmentBlobStore());

        var res = await app.CreateClient().PostAsync($"/intake-sessions/{id}/attachments",
            FilePart("one-too-many.txt", "hi", "text/plain"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(AttachmentLimits.MaxFilesPerSession, (await sessions.GetAsync(id))!.Attachments.Count);
    }

    [Fact]
    public async Task Upload_KeepsTwoFilesOfTheSameNameApart()
    {
        // The operator re-uploads a corrected "questionnaire.pdf". The second must not overwrite the
        // first's blob, or the agent's earlier citation now points at different content.
        var (id, sessions) = await SeededAsync();
        var blobs = new InMemoryAttachmentBlobStore();
        using var app = NewApp(sessions, blobs);
        var client = app.CreateClient();

        await client.PostAsync($"/intake-sessions/{id}/attachments", FilePart("q.txt", "first", "text/plain"));
        await client.PostAsync($"/intake-sessions/{id}/attachments", FilePart("q.txt", "second", "text/plain"));

        var attachments = (await sessions.GetAsync(id))!.Attachments;
        Assert.Equal(2, attachments.Count);
        Assert.NotEqual(attachments[0].BlobPath, attachments[1].BlobPath);
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheAttachmentSurface()
    {
        // Trap 2's regression guard: a missing [FromServices] on any store parameter breaks routing for
        // the WHOLE app, and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryIntakeSessionStore(), new InMemoryAttachmentBlobStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter AttachmentEndpointsTests
```

Expected: FAIL — 404 on the upload route.

- [ ] **Step 3: Implement `src/Smx.Backend/Api/AttachmentEndpoints.cs`**

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Extraction;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// Upload a file into a pre-project interview. Extraction runs HERE, synchronously, inside the request:
/// no agent, no dispatch, no queue (design §5.1). A 25 MB ceiling is what keeps that honest.
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the
        // top of ProjectEndpoints. Without it, minimal APIs mis-infer these as body params and break
        // routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/intake-sessions/{sessionId}/attachments", async (
            string sessionId, IFormFile file, HttpContext http,
            [FromServices] IIntakeSessionStore sessions,
            [FromServices] IAttachmentBlobStore blobs,
            [FromServices] TextExtraction extraction,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.UnprocessableEntity(new { error = "no file was uploaded" });

            if (file.Length > AttachmentLimits.MaxFileBytes)
                return Results.UnprocessableEntity(new
                {
                    error = $"'{file.FileName}' is larger than the {AttachmentLimits.MaxFileBytes / (1024 * 1024)} MB limit.",
                });

            if (await sessions.GetAsync(sessionId, ct) is not { } session) return Results.NotFound();

            if (session.Attachments.Count >= AttachmentLimits.MaxFilesPerSession)
                return Results.UnprocessableEntity(new
                {
                    error = $"this interview already has {AttachmentLimits.MaxFilesPerSession} attachments, " +
                            "which is the limit.",
                });

            // The fileId, not the filename, is what makes the path unique — the operator re-uploads a
            // corrected "questionnaire.pdf" and the first one must stay exactly where it was, or an
            // earlier citation silently starts pointing at different content.
            var fileId = RecordIds.NewAttachmentId();
            var blobPath = AttachmentPaths.Blob(sessionId, fileId, file.FileName);

            // Bytes FIRST, then extraction. If extraction fails, the file is still stored and still
            // visible to the agent as a `failed` attachment it can ask about — which is the whole
            // discipline of §5.2. The other order loses the file whenever the parser dislikes it.
            await using (var upload = file.OpenReadStream())
                await blobs.PutAsync(blobPath, upload, file.ContentType ?? "", ct);

            ExtractionResult result;
            await using (var forExtraction = file.OpenReadStream())
                result = await extraction.ExtractAsync(
                    file.FileName, file.ContentType ?? "", forExtraction, ct);

            string? textPath = null;
            if (result.Status == AttachmentStatus.Extracted)
            {
                textPath = AttachmentPaths.Text(sessionId, fileId);
                await blobs.PutTextAsync(textPath, result.Text, ct);
            }

            var attachment = new SessionAttachment
            {
                FileId = fileId,
                Filename = AttachmentPaths.SafeFilename(file.FileName),
                ContentType = file.ContentType ?? "",
                SizeBytes = file.Length,
                BlobPath = blobPath,
                TextBlobPath = textPath,
                Status = result.Status,
                Error = result.Error,
            };

            // Re-read before appending: an upload can land while an interview turn is in flight, and
            // writing back the copy fetched above would discard whatever the agent recorded meanwhile.
            var latest = await sessions.GetAsync(sessionId, ct) ?? session;
            latest.Attachments.Add(attachment);
            latest.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await sessions.UpsertAsync(latest, ct);

            return Results.Created($"/intake-sessions/{sessionId}/attachments/{fileId}", attachment);
        })
        // REQUIRED. .NET 8 minimal APIs reject a multipart form post with a 400 unless antiforgery is
        // disabled on the endpoint, and the failure reads as a malformed request rather than a missing
        // configuration. This surface is same-origin behind App Gateway and JWT-authenticated; there is
        // no cookie auth for a forged cross-site post to ride on.
        .DisableAntiforgery();
    }
}
```

- [ ] **Step 4: Add `RecordIds.NewAttachmentId()`**

In `src/Smx.Domain/Records/RecordIds.cs`, beside `NewIntakeSessionId()`:

```csharp
    /// Id-safe by construction, like NewIntakeSessionId: it becomes a BLOB PATH SEGMENT, and `N` format
    /// is hex only, so no separator can appear in it however it is concatenated.
    public static string NewAttachmentId() => $"att-{Guid.NewGuid():N}"[..16];
```

- [ ] **Step 5: Register the services in `src/Smx.Backend/Program.cs`**

Beside the other Cosmos/storage registrations, following how `IIntakeSessionStore` is already registered:

```csharp
// The extractor set. Order does not matter (no two claim the same extension), but a new extractor is
// added HERE and nowhere else — TextExtraction picks the first that CanHandle.
builder.Services.AddSingleton(new TextExtraction(
    [new PlainTextExtractor(), new PdfExtractor(), new DocxExtractor(), new XlsxExtractor()]));

if (builder.Configuration["BRONZE_ACCOUNT_NAME"] is { Length: > 0 } bronzeAccount)
{
    var filesystem = builder.Configuration["BRONZE_FILESYSTEM"] ?? "bronze";
    builder.Services.AddSingleton<IAttachmentBlobStore>(_ => new BlobAttachmentStore(
        new DataLakeServiceClient(
            new Uri($"https://{bronzeAccount}.dfs.core.windows.net"), credential)
            .GetFileSystemClient(filesystem)));
}
```

Match how the file already builds its `credential` (`DefaultAzureCredential` / a managed-identity client id) — read `Program.cs` and follow it exactly rather than constructing a second credential. Add `Azure.Storage.Files.DataLake` to `Smx.Backend.csproj` if it is not already available transitively via `Smx.Infrastructure`.

Then call `app.MapAttachmentEndpoints();` beside the other `Map*Endpoints()` calls.

> **Note on the tests:** they inject `IAttachmentBlobStore` via `AddSingleton`, which overrides the conditional registration above. `BRONZE_ACCOUNT_NAME` is unset in tests, so no real client is ever constructed — the same pattern `ORCHESTRATOR_BASE_URL` already uses.

- [ ] **Step 6: Run to verify they pass, then commit**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter AttachmentEndpointsTests
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): the upload endpoint — store the bytes first, record what we could not read"
```

Expected: 6 new tests, suite at 805.

---

## Task 7: `read_attachment`, and the agent's attachment context goes live

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/InterviewTools.cs`, `src/Smx.Orchestrator/Agents/InterviewAgent.cs`, `src/Smx.Orchestrator/Program.cs`
- Test: `src/Smx.Orchestrator.Tests/ReadAttachmentTests.cs`

Plan 1 wrote `InterviewAgent.RenderAttachments` referencing a `read_attachment` tool that did not exist, on the explicit note that `Attachments` was always empty so the branch was unreachable. This task makes both real.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Xunit;

namespace Smx.Orchestrator.Tests;

public class ReadAttachmentTests
{
    private static async Task<(InterviewTools tools, InMemoryIntakeSessionStore sessions,
        InMemoryAttachmentBlobStore blobs, string id)> SetupAsync(params SessionAttachment[] attachments)
    {
        var sessions = new InMemoryIntakeSessionStore();
        var blobs = new InMemoryAttachmentBlobStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Attachments = [.. attachments],
        });
        return (new InterviewTools(sessions, new InMemoryRecordStore(), blobs, id), sessions, blobs, id);
    }

    private static AIFunction Tool(InterviewTools tools, string name) =>
        tools.Tools().OfType<AIFunction>().Single(f => f.Name == name);

    private static Task<object?> InvokeAsync(AIFunction fn, object args) =>
        fn.InvokeAsync(new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(args))!), default).AsTask();

    private static SessionAttachment Extracted(string fileId, string filename, string textPath) => new()
    {
        FileId = fileId, Filename = filename, Status = AttachmentStatus.Extracted,
        BlobPath = $"intake/s/{fileId}/{filename}", TextBlobPath = textPath,
    };

    [Fact]
    public async Task ReadAttachment_ReturnsTheExtractedText()
    {
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-1111", "notes.txt", "intake/s/att-1111/extracted.txt"));
        await blobs.PutTextAsync("intake/s/att-1111/extracted.txt", "the adhesive is water-based");

        var result = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-1111" }))?.ToString();

        Assert.Contains("water-based", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachment_ResolvesThroughTheSession_SoAForgedIdCannotReachAnotherFile()
    {
        // THE safety property of this tool. The fileId comes from a LANGUAGE MODEL. If it were
        // interpolated into a blob path, a hallucinated or crafted value could read another interview's
        // upload — or anything else in `bronze`, which also holds the SDS corpus. The path used is the
        // one STORED on the session's own attachment list; an id that is not in that list is refused
        // without any blob being touched.
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-1111", "notes.txt", "intake/s/att-1111/extracted.txt"));
        await blobs.PutTextAsync("intake/other-session/att-9999/extracted.txt", "SOMEONE ELSE'S PROJECT");

        var result = (await InvokeAsync(Tool(tools, "read_attachment"),
            new { fileId = "../other-session/att-9999" }))?.ToString();

        Assert.DoesNotContain("SOMEONE ELSE", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("att-1111", result, StringComparison.Ordinal);   // it lists what IS available
    }

    [Fact]
    public async Task ReadAttachment_SaysWhyItCannotReadAnUnsupportedFile()
    {
        // The agent must be able to tell "there is nothing in this file" from "I cannot open this
        // file" — only the second is a reason to ask the operator what it shows.
        var (tools, _, _, _) = await SetupAsync(new SessionAttachment
        {
            FileId = "att-2222", Filename = "line-photo.jpg", Status = AttachmentStatus.Unsupported,
            Error = "there is no extractor for .jpg files",
        });

        var result = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-2222" }))?.ToString();

        Assert.Contains("line-photo.jpg", result, StringComparison.Ordinal);
        Assert.Contains("ask the operator", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAttachment_PagesALongDocument_AndSaysThereIsMore()
    {
        var (tools, _, blobs, _) = await SetupAsync(
            Extracted("att-3333", "big.txt", "intake/s/att-3333/extracted.txt"));
        await blobs.PutTextAsync("intake/s/att-3333/extracted.txt",
            new string('a', AttachmentLimits.PageChars) + new string('b', 500));

        var page1 = (await InvokeAsync(Tool(tools, "read_attachment"), new { fileId = "att-3333" }))?.ToString();
        var page2 = (await InvokeAsync(Tool(tools, "read_attachment"),
            new { fileId = "att-3333", page = 2 }))?.ToString();

        Assert.Contains("page 1 of 2", page1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bbb", page1, StringComparison.Ordinal);
        Assert.Contains("bbb", page2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentSchema_DoesNotRequireThePage_SoAOneArgCallBinds()
    {
        // Trap 1: AIFunctionFactory emits a parameter WITHOUT a default as `required` regardless of the
        // description, and the binder then rejects every ordinary one-argument call before the body
        // runs. This is exactly how a tool ships dead on arrival.
        var (tools, _, _, _) = await SetupAsync();
        var schema = Tool(tools, "read_attachment").JsonSchema.ToString();

        Assert.Contains("fileId", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\"", RequiredSectionOf(schema), StringComparison.Ordinal);
    }

    /// The `required` array of the tool's JSON schema, or "" when there is none.
    private static string RequiredSectionOf(string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.TryGetProperty("required", out var req) ? req.ToString() : "";
    }

    [Fact]
    public async Task RenderAttachments_NamesAnUnreadableFileWithItsStatus()
    {
        // Design §5.2: an unreadable file is a VISIBLE FACT, never silence. If it did not reach the
        // agent's context the operator would never be asked what it shows, and the analysis would run
        // as though the file said nothing.
        var session = new IntakeSessionDoc
        {
            Id = "isx-1", SessionId = "isx-1", CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Attachments =
            [
                new() { FileId = "att-2222", Filename = "line-photo.jpg",
                        ContentType = "image/jpeg", Status = AttachmentStatus.Unsupported },
            ],
        };

        var rendered = InterviewAgent.RenderAttachments(session);

        Assert.Contains("line-photo.jpg", rendered, StringComparison.Ordinal);
        Assert.Contains("CANNOT", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
```

> `InterviewTools`' constructor gains a fourth parameter (`IAttachmentBlobStore`). **This breaks every existing construction site** — `InterviewToolsTests` and `InterviewEndpoints`. Update them; the compiler will list them.
>
> `InterviewAgent.RenderAttachments` is currently `private`. Make it `internal static` — `Smx.Orchestrator` already has `InternalsVisibleTo("Smx.Orchestrator.Tests")` for exactly this kind of pure, high-value assertion.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ReadAttachmentTests
```

Expected: FAIL — `InterviewTools` has no such constructor and no such tool.

- [ ] **Step 3: Implement the tool**

In `src/Smx.Orchestrator/Agents/InterviewTools.cs`, change the primary constructor to take the blob store:

```csharp
public sealed class InterviewTools(
    IIntakeSessionStore sessions, IRecordStore records, IAttachmentBlobStore blobs, string sessionId)
```

Add to the `Tools()` list, after `write_summary`:

```csharp
        AIFunctionFactory.Create(ReadAttachmentAsync, "read_attachment",
            "Read the text of a file the operator attached to this interview. `fileId` is the id shown " +
            "beside the filename in the ATTACHMENTS section of your context — never invent one. " +
            "Long documents are paged: `page` defaults to 1 and the reply tells you how many there are. " +
            "Read a file BEFORE asking the operator about what might be in it."),
```

And the method:

```csharp
    /// `page` defaults to 1 because AIFunctionFactory emits a parameter WITHOUT a default as `required`
    /// in the JSON schema regardless of the description — the binder would then reject every ordinary
    /// one-argument call before this body ran. Same trap as `confidence` on record_finding.
    public async Task<string> ReadAttachmentAsync(string fileId, int page = 1, CancellationToken ct = default)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";

        // Resolved THROUGH the session's own attachment list, never by building a path out of fileId.
        // fileId arrives from a language model; interpolating it into a blob path would let a
        // hallucinated or crafted value reach another interview's upload — or anything else in the
        // `bronze` container, which also holds the SDS corpus. The path used below is the one this
        // session recorded at upload.
        if (session.Attachments.FirstOrDefault(a =>
                string.Equals(a.FileId, fileId, StringComparison.Ordinal)) is not { } attachment)
            return session.Attachments.Count == 0
                ? "there are no attachments on this interview."
                : $"'{fileId}' is not an attachment on this interview. The ones there are: " +
                  $"{string.Join(", ", session.Attachments.Select(a => $"{a.FileId} ({a.Filename})"))}.";

        if (attachment.Status != AttachmentStatus.Extracted || attachment.TextBlobPath is not { } textPath)
            return $"'{attachment.Filename}' could not be read ({attachment.Error ?? attachment.Status}). " +
                   "Ask the operator what it contains — their answer is a real answer, and you should " +
                   "record it with record_finding noting which file it describes.";

        if (await blobs.GetTextAsync(textPath, ct) is not { } text)
            return $"'{attachment.Filename}' was extracted but its text is missing from storage. " +
                   "Tell the operator, and ask them what it contains.";

        var pages = Math.Max(1, (int)Math.Ceiling((double)text.Length / AttachmentLimits.PageChars));
        var index = Math.Clamp(page, 1, pages);
        var start = (index - 1) * AttachmentLimits.PageChars;
        var slice = text.Substring(start, Math.Min(AttachmentLimits.PageChars, text.Length - start));

        Trail.Add($"read_attachment({attachment.FileId}, page {index})");
        return $"{attachment.Filename} — page {index} of {pages}\n\n{slice}" +
               (index < pages ? $"\n\n[continues — call read_attachment(\"{fileId}\", {index + 1}) for more]" : "");
    }
```

> `Trail.Add` is called directly here rather than through `MutateAsync`, because reading an attachment changes nothing about the session and should not provoke a write.

- [ ] **Step 4: Make `RenderAttachments` internal and register the store**

In `InterviewAgent.cs`, change `private static string RenderAttachments` to `internal static string RenderAttachments`, and delete the Plan-1 note saying `read_attachment` does not exist yet — it does now. Keep the rest of the wording: Plan 1 wrote it for this moment.

In `src/Smx.Orchestrator/Program.cs`, register `IAttachmentBlobStore` exactly as Task 6 does for the backend (same `BRONZE_ACCOUNT_NAME` / `BRONZE_FILESYSTEM` configuration, same credential the file already builds), and update the `InterviewTools` construction in `src/Smx.Orchestrator/Api/InterviewEndpoints.cs` to pass it (resolve it with `[FromServices] IAttachmentBlobStore blobs`).

Add a DI test beside the existing one in `OrchestratorHostWiringTests`:

```csharp
    [Fact]
    public void Host_Resolves_TheAttachmentBlobStore()
    {
        // dotnet build proves nothing about DI: a missing registration is a runtime failure at the
        // first resolve, which for this host means in production, mid-interview.
        var services = new ServiceCollection();
        OrchestratorHost.ConfigureServices(services, MinimalConfig());
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IAttachmentBlobStore>());
    }
```

Match the file's real config helper name, and make sure `MinimalConfig()` supplies `BRONZE_ACCOUNT_NAME` so the conditional registration actually fires — otherwise this test pins nothing.

- [ ] **Step 5: Run to verify they pass, then commit**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter "ReadAttachmentTests|Host_Resolves"
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): read_attachment — resolved through the session, never from a model-supplied path"
```

Expected: 7 new tests, suite at 812. The Plan-1 tests `NoToolSchema_MentionsTheSessionId` and `ThereIsNoWebOrRegulatorySearch_AndNothingThatStartsTheAnalysis` must **still pass** — `read_attachment` adds no session parameter and is not a search tool.

---

## Task 8: Infra — the apps need to know where `bronze` is

**Files:**
- Modify: `infra/modules/compute.bicep`, `infra/main.bicep`, `infra/single-rg/modules/compute.bicep`, `infra/single-rg/main.bicep`

**No RBAC change is needed** and you should verify that rather than take it on trust: `infra/modules/data.bicep` grants **Storage Blob Data Contributor** (`ba92f5b4-…`) on the storage account to `uamiPrincipalId` — the single shared workload identity used by the backend, the orchestrator and the Functions app alike. Confirm that is still true before concluding it.

- [ ] **Step 1: Add the parameter and the environment variables**

In `infra/modules/compute.bicep`, beside the other optional params:

```bicep
@description('Storage account holding the bronze filesystem — interview attachments are written there.')
param bronzeAccountName string = ''
```

Add both variables to `sharedEnv` (BOTH apps need them: the backend writes attachments, the orchestrator reads their extracted text for `read_attachment`):

```bicep
  { name: 'BRONZE_ACCOUNT_NAME', value: bronzeAccountName }
  { name: 'BRONZE_FILESYSTEM', value: 'bronze' }
```

Read how `sharedEnv` is assembled and follow it; if the frontend also consumes `sharedEnv`, confirm that handing it these two values is harmless (it is a static nginx image) or scope them to the two apps that need them.

- [ ] **Step 2: Pass it from both main templates**

In `infra/main.bicep`, in the `module compute` params block (around line 270):

```bicep
    bronzeAccountName: data.outputs.storageName
```

Do the same in `infra/single-rg/main.bicep`. Both already pass exactly this value to the functions module (`bronzeAccountName: data.outputs.storageName`), so the output exists and the name is right.

- [ ] **Step 3: Validate both variants compile**

```bash
cd /home/elimeshi/projects/repos/SMX
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: exit 0 for both. A pre-existing `BCP037` warning about `modelProviderData` in `ai.bicep` is unrelated — it is there before this change and must still be the ONLY warning after.

- [ ] **Step 4: Check the script twins**

`infra/scripts/` are **bash + PowerShell twin pairs** — fix a bug in one and fix it in the other. Read `infra/scripts/README.md`. If `dev-local-setup.*` or `dev-local.*` set backend environment variables, they need `BRONZE_ACCOUNT_NAME`/`BRONZE_FILESYSTEM` too, or local uploads fail with an unregistered `IAttachmentBlobStore` — update **both** twins if so.

- [ ] **Step 5: Commit**

```bash
git add -A infra/
git commit -m "infra(intake): the apps learn where bronze is, for interview attachments"
```

---

## Task 9: Full-suite verification

- [ ] **Step 1: Build everything**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet build src/Smx.Backend.sln
dotnet build src/Smx.Functions.sln
```

Expected: both succeed with **0 warnings**. A warning introduced by this plan is a defect, not noise.

- [ ] **Step 2: Run every test**

```bash
dotnet test src/Smx.Backend.sln
dotnet test src/Smx.Functions.sln
```

Expected: `Smx.Backend.sln` at **baseline 765 + 47 or more**; `Smx.Functions.sln` unchanged at 177. If the count is *lower* than baseline, a test was deleted — find out which and why before continuing.

- [ ] **Step 3: Confirm the safety properties by name**

```bash
dotnet test src/Smx.Backend.sln --filter "ReadAttachment_ResolvesThroughTheSession_SoAForgedIdCannotReachAnotherFile|Blob_CannotEscapeTheSessionFolder_EvenWithATraversingFilename|Upload_RecordsAnUnreadableFile_RatherThanRejectingIt|RenderAttachments_NamesAnUnreadableFileWithItsStatus|ReadAttachmentSchema_DoesNotRequireThePage_SoAOneArgCallBinds|NoToolSchema_MentionsTheSessionId|ThereIsNoWebOrRegulatorySearch_AndNothingThatStartsTheAnalysis"
```

Expected: **7 passed**. The first five are this plan's; the last two are Plan 1's, re-run here because Task 7 changes the toolset and they are what proves it did not widen the agent's reach.

- [ ] **Step 4: Confirm the tree is clean**

```bash
git status --short
```

Expected: empty.

---

## What Plan 2 deliberately does not do

- **No OCR, no vision, no scanned PDFs.** A scan comes back `failed` with "no text layer", which the agent surfaces as a question. Adding OCR later is a new `ITextExtractor` and nothing else — no schema change, no agent change, no endpoint change. That is the whole reason the interface exists.
- **No frontend.** There is no upload control yet; the endpoint is reachable but nothing calls it. Plan 3 builds the interview screen, including drag-and-drop. Until then this is testable by `curl` and by the endpoint tests.
- **No re-extraction of existing attachments.** When a new extractor is added, files already uploaded keep the status they were given. A re-extract endpoint is worth having and is not needed yet.
- **No virus scanning.** Single-operator, internal tool, files the operator chose themselves. Worth revisiting if the deployment model ever changes.
