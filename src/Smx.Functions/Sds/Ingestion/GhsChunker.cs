using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Ingestion;

public sealed class GhsChunker
{
    public IReadOnlyList<(string Section, string Content)> Chunk(string text)
    {
        var headers = GhsSections.FindHeaders(text);   // shared dialect-aware parser

        var chunks = new List<(string, string)>();
        for (var i = 0; i < headers.Count; i++)
        {
            var start = headers[i].Index;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var content = text[start..end].Trim();
            chunks.Add((headers[i].Number.ToString(), content));
        }
        return chunks;
    }
}
