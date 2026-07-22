using System.Text;
using Smx.Backend.Extraction;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class ExtractorTests
{
    private static Stream Bytes(byte[] b) => new MemoryStream(b);
    private static Stream Utf8(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".json")]
    [InlineData(".xml")]
    [InlineData(".csv")]
    [InlineData(".tsv")]
    public void PlainText_HandlesEveryTextExtension(string ext) =>
        Assert.True(new PlainTextExtractor().CanHandle("application/octet-stream", ext));

    [Fact]
    public void PlainText_DoesNotClaimBinaryFormats()
    {
        var x = new PlainTextExtractor();
        Assert.False(x.CanHandle("application/pdf", ".pdf"));
        Assert.False(x.CanHandle("image/jpeg", ".jpg"));
    }

    [Fact]
    public async Task PlainText_ReadsTheContentAndKeepsItsLines()
    {
        var result = await new PlainTextExtractor().ExtractAsync(Utf8("line one\nline two"), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.Contains("line one", result.Text, StringComparison.Ordinal);
        Assert.Contains("line two", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainText_StripsAUtf8Bom_SoTheFirstFieldNameIsNotCorrupted()
    {
        // A CSV saved by Excel starts with a BOM. Left in place it becomes an invisible prefix on the
        // first header, and every downstream comparison against that header silently stops matching.
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("cas,name")).ToArray();

        var result = await new PlainTextExtractor().ExtractAsync(Bytes(withBom), default);

        Assert.StartsWith("cas", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainText_TruncatesAtTheCeiling_AndSaysItDidSo()
    {
        // Silent truncation reads as "that was the whole file" — which is the false-pass shape this
        // system exists to avoid. The marker is what lets the agent know to ask.
        var huge = new string('x', AttachmentLimits.MaxExtractedChars + 5_000);

        var result = await new PlainTextExtractor().ExtractAsync(Utf8(huge), default);

        Assert.Equal(AttachmentStatus.Extracted, result.Status);
        Assert.True(result.Text.Length <= AttachmentLimits.MaxExtractedChars + 200,
            $"text was {result.Text.Length} chars — the ceiling was not applied");
        Assert.Contains("truncated", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
