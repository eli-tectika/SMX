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
