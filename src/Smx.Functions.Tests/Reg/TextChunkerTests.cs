using System.Linq;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class TextChunkerTests
{
    [Fact]
    public void Empty_or_whitespace_yields_no_chunks()
    {
        Assert.Empty(TextChunker.Chunk(""));
        Assert.Empty(TextChunker.Chunk("   \n\n  \t "));
    }

    [Fact]
    public void Short_prose_yields_a_single_chunk()
    {
        var chunks = TextChunker.Chunk("A short paragraph.\n\nAnother short one.");
        Assert.Single(chunks);
        Assert.Contains("short paragraph", chunks[0]);
        Assert.Contains("Another short one", chunks[0]);
    }

    [Fact]
    public void Long_prose_splits_into_bounded_chunks_with_overlap()
    {
        // 40 paragraphs of ~100 chars → well over one 1500-char chunk.
        var paras = Enumerable.Range(0, 40)
            .Select(i => $"Paragraph number {i}. " + new string('x', 90));
        var text = string.Join("\n\n", paras);

        var chunks = TextChunker.Chunk(text, maxChars: 1500, overlap: 150);

        Assert.True(chunks.Count > 1);
        // Each chunk is bounded by maxChars + the overlap window (+ a joining newline).
        Assert.All(chunks, c => Assert.True(c.Length <= 1500 + 150 + 1, $"chunk too long: {c.Length}"));
        // Overlap: the start of chunk[1] repeats the tail of chunk[0].
        var tail = chunks[0].Substring(chunks[0].Length - 150);
        Assert.StartsWith(tail, chunks[1]);
    }

    [Fact]
    public void An_oversize_paragraph_is_hard_split()
    {
        var giant = new string('a', 4000); // one paragraph, no blank-line boundaries
        var chunks = TextChunker.Chunk(giant, maxChars: 1000, overlap: 100);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000 + 100 + 1));
    }

    [Fact]
    public void Is_deterministic()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Section {i}: " + new string('y', 120)));
        var a = TextChunker.Chunk(text);
        var b = TextChunker.Chunk(text);
        Assert.Equal(a, b);
    }
}
