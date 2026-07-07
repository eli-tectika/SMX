using Smx.Functions.Sds.Ingestion;
using Xunit;

public class GhsChunkerTests
{
    private static string Sample() => File.ReadAllText("Resources/sample_sds.txt");

    [Fact]
    public void Splits_into_sixteen_tagged_sections()
    {
        var chunks = new GhsChunker().Chunk(Sample());
        Assert.Equal(16, chunks.Count);
        Assert.Equal("1", chunks[0].Section);
        Assert.Equal("16", chunks[^1].Section);
    }

    [Fact]
    public void Section_three_chunk_contains_composition_text()
    {
        var chunks = new GhsChunker().Chunk(Sample());
        var s3 = chunks.Single(c => c.Section == "3");
        Assert.Contains("Composition", s3.Content);
    }

    [Fact]
    public void Ignores_preamble_before_section_one()
    {
        var chunks = new GhsChunker().Chunk("garbage header\nSECTION 1: Identification\nbody");
        Assert.Single(chunks);
        Assert.Equal("1", chunks[0].Section);
        Assert.DoesNotContain("garbage", chunks[0].Content);
    }
}
