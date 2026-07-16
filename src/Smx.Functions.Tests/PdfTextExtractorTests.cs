using Smx.Functions.Sds.Ingestion;
using Xunit;

// Regression guard for the live 2026-07-16 finding: PdfPig's page.Text concatenates a page into a
// single line, so the validator's line-anchored '^\s*SECTION n' regex matched nothing and every
// real SDS was rejected with "only 0 GHS sections found". The extractor must preserve line
// structure. Fixture: a real 16-section GHS SDS (Nd2O3, CAS 1313-97-9, public supplier SDS).
public class PdfTextExtractorTests
{
    [Fact]
    public void Extract_preserves_line_structure_so_a_real_sds_passes_validation()
    {
        var pdf = File.ReadAllBytes("Resources/real-sds-nd2o3.pdf");
        var text = new PdfTextExtractor().Extract(pdf);

        var result = new SdsValidator(10).Validate(
            text, "1313-97-9", "chemblink.com", new HashSet<string> { "chemblink.com" });

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void Extract_yields_chunkable_ghs_sections_from_a_real_sds()
    {
        var pdf = File.ReadAllBytes("Resources/real-sds-nd2o3.pdf");
        var text = new PdfTextExtractor().Extract(pdf);

        var chunks = new GhsChunker().Chunk(text);

        Assert.True(chunks.Count >= 10, $"only {chunks.Count} chunks");
        Assert.Contains(chunks, c => c.Section == "1");
        Assert.Contains(chunks, c => c.Section == "16");
    }
}
