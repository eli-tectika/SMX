using System.Text;
using System.Text.RegularExpressions;

namespace Smx.Functions.Reg.Ingestion;

// Free-text chunker for prose regulatory bodies (the seed corpus — plain articles/overviews, not CSV rows).
// Packs blank-line-delimited paragraphs into bounded-size chunks with a small overlap so a citation window
// keeps continuity across a boundary. Deterministic: the same input always yields the same chunk list, so
// chunk ids ("{docId}#{i}") are stable and re-imports merge rather than duplicate.
public static class TextChunker
{
    public const int DefaultMaxChars = 1500;
    public const int DefaultOverlap = 150;

    public static IReadOnlyList<string> Chunk(string text, int maxChars = DefaultMaxChars, int overlap = DefaultOverlap)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        if (maxChars < 1) maxChars = DefaultMaxChars;
        if (overlap < 0 || overlap >= maxChars) overlap = Math.Min(DefaultOverlap, maxChars / 2);

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var paragraphs = Regex.Split(normalized, @"\n[ \t]*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<string>();
        var cur = new StringBuilder();

        // Emit the current buffer as a chunk, prefixing the tail of the previous chunk as the overlap window.
        // `cur` only ever holds freshly packed content (never a prefix), so packing decisions honour maxChars.
        void Emit()
        {
            if (cur.Length == 0) return;
            var content = cur.ToString();
            cur.Clear();
            if (overlap > 0 && chunks.Count > 0)
            {
                var prev = chunks[^1];
                var window = prev.Length > overlap ? prev[^overlap..] : prev;
                chunks.Add(window + "\n" + content);
            }
            else
            {
                chunks.Add(content);
            }
        }

        foreach (var para in paragraphs)
        {
            if (para.Length > maxChars)
            {
                Emit(); // close any packed paragraphs before hard-splitting the oversize one
                for (var i = 0; i < para.Length; i += maxChars)
                {
                    cur.Append(para, i, Math.Min(maxChars, para.Length - i));
                    Emit();
                }
                continue;
            }

            if (cur.Length > 0 && cur.Length + 1 + para.Length > maxChars) Emit();
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(para);
        }

        Emit();
        return chunks;
    }
}
