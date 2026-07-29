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

            // Truncate FIRST, then mark: the note goes at the very end, and a document long enough
            // to be cut would otherwise have the note cut off with it. Counting after the cut also
            // means the number describes the text the agent can actually see.
            return ExtractionResult.Extracted(MarkUnmappedGlyphs(PlainTextExtractor.Truncate(text)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // NEVER let this escape: an unreadable file must become a recorded status the agent asks
            // about, not a 500 that loses the upload the operator just made.
            return ExtractionResult.Failed(Explain(e));
        }
    }

    /// What the operator and the agent are told when a PDF will not open.
    ///
    /// This string is not a log line — the chip puts it in front of the operator, and
    /// InterviewAgent.RenderAttachments puts it in front of the agent, whose next move is to ask
    /// the operator about the file. PdfPig's own wording fails both readers: "none of the provided
    /// passwords were the user or owner password" invites a password this system has nowhere to
    /// accept (live on 2026-07-29 the agent duly offered to take one), and "could not find the
    /// version header comment" describes the parser rather than the file. Anything unrecognised
    /// still carries the raw message through — a wrong guess about the cause would be worse than an
    /// ugly sentence.
    private static string Explain(Exception e) => e switch
    {
        UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException =>
            "this PDF is password-protected, and it cannot be opened without the password. " +
            "Save an unprotected copy and upload that one instead.",
        UglyToad.PdfPig.Core.PdfDocumentFormatException =>
            "this file is not a PDF, whatever its name says — nothing in it parses as one.",
        _ => $"could not read this PDF ({e.Message})",
    };

    /// A PDF that embeds a font SUBSET carries only the glyphs it uses, frequently with no ToUnicode
    /// map for them; those glyphs extract as U+0000. pypdf produces the same NULs on the same file,
    /// so this is the document's own encoding rather than anything PdfPig does — changing library
    /// would not change the result.
    ///
    /// Deleting them is the tempting fix and the wrong one: "confirmed" would become "conrmed", a
    /// word that reads as a typo to correct rather than a character that is gone. Worse, this is
    /// glyph-level, so it takes digits too — a numbered list came back with no numbers at all, and
    /// nothing stops the same font subset eating a digit inside "50 ppm". U+FFFD says a character
    /// was here and could not be decoded, and the count says how much of the file that happened to.
    ///
    /// TWIN: Smx.Functions.Sds.Ingestion.PdfTextExtractor does the same stripping for the SDS
    /// corpus. Fix a bug in one and fix it in the other.
    internal static string MarkUnmappedGlyphs(string text)
    {
        var lost = text.Count(c => c == '\0');
        if (lost == 0) return text;

        return text.Replace('\0', '�') +
               $"\n\n[... {lost} character(s) in this PDF could not be mapped to text and are shown " +
               "as � — it embeds a font subset with no Unicode map. Anything that matters here, " +
               "digits especially, should be confirmed with the operator rather than read off.]";
    }
}
