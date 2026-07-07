using System.Text;
using UglyToad.PdfPig;

namespace Smx.Functions.Sds.Ingestion;

public sealed class PdfTextExtractor : IPdfTextExtractor
{
    public string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages()) sb.AppendLine(page.Text);
        return sb.ToString();
    }
}
