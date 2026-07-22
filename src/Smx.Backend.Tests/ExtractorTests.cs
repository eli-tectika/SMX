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
}
