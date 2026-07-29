using Smx.Functions.Sds.Ingestion;
using Xunit;

// Regression guard for the 2026-07-16 allowlist shakedown: Thermo Fisher / Alfa Aesar (and
// Materion, and others) title GHS sections "1. Identification" / "4. First-aid measures" —
// not "SECTION 1". The validator and chunker must accept both dialects, or every SDS from
// the one supplier we can actually fetch is rejected with "only 0 GHS sections found".
// Fixture: the real Alfa Aesar Nd2O3 SDS served by fishersci.com (partNumber AA11250).
public class GhsDialectTests
{
    private static string NumberedDotSds()
    {
        var s = new System.Text.StringBuilder();
        var titles = new[] { "Identification", "Hazard(s) identification", "Composition/information on ingredients",
            "First-aid measures", "Fire-fighting measures", "Accidental release measures", "Handling and storage",
            "Exposure controls/personal protection", "Physical and chemical properties", "Stability and reactivity",
            "Toxicological information", "Ecological information", "Disposal considerations", "Transport information",
            "Regulatory information", "Other information" };
        for (var i = 0; i < titles.Length; i++)
        {
            s.AppendLine($"{i + 1}. {titles[i]}");
            s.AppendLine($"Body of section {i + 1}. CAS-No 1313-97-9.");
            if (i == 3)   // numbered list INSIDE section 4 — must not split the chunk
            {
                s.AppendLine("1. Rinse with water");
                s.AppendLine("2. Seek medical attention");
            }
        }
        return s.ToString();
    }

    [Fact]
    public void Validator_accepts_numbered_dot_dialect()
    {
        var allow = (IReadOnlySet<string>)new HashSet<string> { "fishersci.com" };
        var r = new SdsValidator(10).Validate(NumberedDotSds(), "1313-97-9");
        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void Chunker_splits_numbered_dot_dialect_without_splitting_on_inline_lists()
    {
        var chunks = new GhsChunker().Chunk(NumberedDotSds());
        Assert.Equal(16, chunks.Count);
        Assert.Equal("1", chunks[0].Section);
        Assert.Equal("16", chunks[^1].Section);
        var s4 = chunks.Single(c => c.Section == "4");
        Assert.Contains("Rinse with water", s4.Content);      // list stayed inside section 4
    }

    [Fact]
    public void Real_alfa_sds_via_real_extractor_passes_validation_and_chunks()
    {
        var pdf = File.ReadAllBytes("Resources/real-sds-alfa-nd2o3.pdf");
        var text = new PdfTextExtractor().Extract(pdf);

        var allow = (IReadOnlySet<string>)new HashSet<string> { "fishersci.com" };
        var v = new SdsValidator(10).Validate(text, "1313-97-9");
        Assert.True(v.Ok, v.Reason);

        var chunks = new GhsChunker().Chunk(text);
        Assert.True(chunks.Count >= 10, $"only {chunks.Count} chunks");
    }
}
