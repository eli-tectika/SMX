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
        return sb.ToString();
    }
}
