using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Ingestion;

public sealed class GhsChunker
{
    private static readonly Regex Header = new(@"(?im)^\s*SECTION\s+(\d{1,2})\b.*$", RegexOptions.Compiled);

    public IReadOnlyList<(string Section, string Content)> Chunk(string text)
    {
        var matches = Header.Matches(text)
            .Where(m => int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 16)
            .ToList();

        var chunks = new List<(string, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var section = matches[i].Groups[1].Value;
            var content = text[start..end].Trim();
            chunks.Add((section, content));
        }
        return chunks;
    }
}
