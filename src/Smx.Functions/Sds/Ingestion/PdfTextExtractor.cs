using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Smx.Functions.Sds.Ingestion;

public sealed class PdfTextExtractor : IPdfTextExtractor
{
    public string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        // ContentOrderTextExtractor, NOT page.Text: page.Text concatenates the page into a single
        // line, which blinded the validator's line-anchored GHS-section regex on every real SDS
        // ("only 0 GHS sections found", live 2026-07-16). This extractor preserves line structure.
        foreach (var page in doc.GetPages()) sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        return MarkUnmappedGlyphs(sb.ToString());
    }

    /// A PDF embedding a font SUBSET usually carries no ToUnicode map for it, and those glyphs come
    /// out as U+0000. pypdf reproduces the same NULs on the same file, so it is the document's own
    /// encoding, not PdfPig — no library swap avoids it.
    ///
    /// This text becomes SDS corpus chunks and then AI Search documents, so a NUL would be a
    /// permanent artefact of a retrievable source. Marked rather than deleted: deleting turns
    /// "confirmed" into "conrmed", which reads as a typo to correct rather than as a lost
    /// character — and the loss is glyph-level, so it reaches digits too.
    ///
    /// TWIN: Smx.Backend.Extraction.PdfExtractor.MarkUnmappedGlyphs, which additionally appends a
    /// note for the interview agent. There is no note here: nothing reads this text conversationally,
    /// and an injected sentence would become a chunk that no source document contains.
    internal static string MarkUnmappedGlyphs(string text) => text.Replace('\0', '�');
}
