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

    /// TWIN of Smx.Backend.Tests.ExtractorTests.Pdf_NeverHandsBackNulCharacters. A PDF that embeds a
    /// font SUBSET usually has no ToUnicode map for it, and those glyphs extract as U+0000 (pypdf
    /// reproduces the same NULs — it is the document's encoding, not PdfPig). Here the text does not
    /// go to an agent, it goes into the SDS corpus and on into the AI Search index, so a NUL becomes
    /// a permanent artefact of a retrievable document — and a swallowed digit in an SDS is a
    /// regulatory claim that cites something the source never said.
    [Fact]
    public void Extract_never_yields_nul_characters_from_a_subsetted_font()
    {
        var pdf = File.ReadAllBytes("Resources/subsetted-fonts.pdf");

        var text = new PdfTextExtractor().Extract(pdf);

        Assert.DoesNotContain('\0', text);
        // Marked, not deleted: dropping them turns "confirmed" into "conrmed", which reads as a typo
        // rather than as a character that is missing.
        Assert.Contains('�', text);
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
