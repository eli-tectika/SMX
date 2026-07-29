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

    /// A PDF that embeds a SUBSET of a font carries only the glyphs it uses, and often no ToUnicode
    /// map for them. Those glyphs extract as U+0000 — pypdf reproduces the same NULs on the same
    /// file, so this is the document's encoding, not a PdfPig bug, and no library swap fixes it.
    /// Found live on 2026-07-29: "first" arrived as "\0rst", "flag" as "\0ag", and a numbered list's
    /// numerals were gone entirely — while the file was still reported `extracted` with no signal
    /// that a single character had been lost.
    [Fact]
    public async Task Pdf_NeverHandsBackNulCharacters_WhenAFontSubsetHasNoUnicodeMap()
    {
        // A NUL is not merely ugly. It rides into the session document, into Cosmos, and into the
        // agent's prompt, where it reads as "nothing was here" rather than "something was lost".
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/subsetted-fonts.pdf"), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.DoesNotContain('\0', result.Text);
    }

    [Fact]
    public async Task Pdf_MarksEachUnmappableGlyph_SoALostCharacterCannotReadAsAWholeWord()
    {
        // Deleting them would turn "confirmed" into "conrmed" — a word that looks like a typo the
        // reader should correct, rather than a character that is MISSING. U+FFFD is the standard
        // "a character was here that could not be decoded", and it survives into the prompt as one.
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/subsetted-fonts.pdf"), default);

        Assert.Contains('�', result.Text);
    }

    [Fact]
    public async Task Pdf_SaysHowManyCharactersItCouldNotMap_RatherThanLosingThemSilently()
    {
        // The same discipline as Truncate: a document that lost characters and looks complete is a
        // document the agent will confidently reason about. Announced, so it can ask instead.
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/subsetted-fonts.pdf"), default);

        Assert.Contains("could not be mapped", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pdf_DoesNotCryUnmapped_OverAFileWhoseGlyphsAllResolved()
    {
        // The note has to mean something. A warning on every SDS is a warning the agent learns to
        // ignore on the one file where it mattered.
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/real-sds-nd2o3.pdf"), default);

        Assert.DoesNotContain("could not be mapped", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('�', result.Text);
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

    /// The `error` on a failed attachment is not a log line. The chip shows it to the operator and
    /// RenderAttachments shows it to the agent, whose next move is to ask the operator about the
    /// file — so the sentence has to describe something they can actually do.
    [Fact]
    public async Task Pdf_TellsTheOperatorAProtectedFileIsProtected_NotWhichPasswordItTried()
    {
        // PdfPig says "none of the provided passwords were the user or owner password", which reads
        // as an invitation to supply one. There is no field to supply one in, and live on
        // 2026-07-29 the agent duly offered to accept a "decryption password" it could not use.
        var result = await new PdfExtractor()
            .ExtractAsync(File.OpenRead("Resources/password-protected.pdf"), default);

        Assert.Equal(AttachmentStatus.Failed, result.Status);
        Assert.Contains("password-protected", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner password", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pdf_SaysAFileIsNotAPdf_RatherThanQuotingTheParserAtTheOperator()
    {
        // "Could not find the version header comment at the start of the document" describes the
        // parser's disappointment. What the operator needs to know is that the thing they attached
        // is not the kind of file its name claims.
        var result = await new PdfExtractor().ExtractAsync(Utf8("this is not a pdf"), default);

        Assert.Equal(AttachmentStatus.Failed, result.Status);
        Assert.Contains("not a PDF", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version header", result.Error!, StringComparison.OrdinalIgnoreCase);
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

    private static TextExtraction AllExtractors() => new(
        [new PlainTextExtractor(), new PdfExtractor(), new DocxExtractor(), new XlsxExtractor()]);

    [Fact]
    public async Task Extraction_ReportsUnsupported_ForAFormatWithNoExtractor_AndNamesIt()
    {
        // The agent is shown this. "unsupported" alone tells the operator nothing; naming the type is
        // what turns a dead file into a question worth asking.
        var result = await AllExtractors().ExtractAsync("line-photo.jpg", "image/jpeg", Utf8("...."), default);

        Assert.Equal(AttachmentStatus.Unsupported, result.Status);
        Assert.Contains(".jpg", result.Error!, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("detonated", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extraction_ReportsUnsupported_ForAFileWithNoExtensionAtAll_AndNamesTheFile()
    {
        // "README" has no extension for CanHandle to match against. This must come back a recorded
        // `unsupported` naming the file itself (there is no extension to name), not an exception and
        // not a silent match against whichever extractor happens to be first in the list.
        var result = await AllExtractors().ExtractAsync("README", "application/octet-stream",
            Utf8("some text"), default);

        Assert.Equal(AttachmentStatus.Unsupported, result.Status);
        Assert.Contains("README", result.Error!, StringComparison.Ordinal);
    }

    private sealed class ThrowingExtractor : ITextExtractor
    {
        public bool CanHandle(string contentType, string extension) => extension == ".boom";
        public Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct) =>
            throw new InvalidOperationException("detonated");
    }
}
