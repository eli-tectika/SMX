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
