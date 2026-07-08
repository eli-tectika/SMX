using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Seeding;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class SeedImporterTests
{
    private sealed class Harness
    {
        public FakeBronzeStore Bronze = new();
        public InMemoryRegSilverStore Silver = new();
        public InMemoryRegStateStore State = new();
        public FakeRegSearchClient Search = new();

        public SeedImporter Build() => new(Bronze, Silver, State, new FakeEmbedder(), Search,
            NullLogger<SeedImporter>.Instance);
    }

    // Builds a deterministic temp fixture: one region with a body .txt + a matching _metadata.txt + a .pdf,
    // plus a `._junk.txt` and a `__MACOSX` folder — both of which must be ignored. Does NOT touch the real
    // C:\SMX\Regulations 2 corpus.
    private static string BuildFixture()
    {
        var root = Directory.CreateTempSubdirectory("smx-seed-test").FullName;
        var region = Path.Combine(root, "02_European_Union");
        Directory.CreateDirectory(region);

        File.WriteAllText(Path.Combine(region, "EU_BPR_528-2012.txt"),
            "SOURCE URL: https://eur-lex.europa.eu/eli/reg/2012/528\n\n" +
            "Biocidal Products Regulation\n============================\n\n" +
            "Article 1. Subject matter. " + new string('x', 200) + "\n\n" +
            "Article 2. Scope. " + new string('y', 200) + "\n\n" +
            "Published: 2012-05-22\n");

        File.WriteAllText(Path.Combine(region, "EU_BPR_528-2012_metadata.txt"),
            "Official Title:\nRegulation (EU) No 528/2012 concerning the making available on the market and use of biocidal products\n\n" +
            "CELEX Number: 32012R0528\n" +
            "Document Date: 2012-05-22\n" +
            "Source: EUR-Lex (https://eur-lex.europa.eu/eli/reg/2012/528/oj)\n" +
            "Scraped: 2026-06-18\n");

        // Official PDF (provenance only — never ingested as a body).
        File.WriteAllText(Path.Combine(region, "EU_BPR_528-2012.pdf"), "%PDF-1.7 fake");

        // Junk that MUST be skipped.
        File.WriteAllText(Path.Combine(region, "._junk.txt"), "resource fork junk");
        var macosx = Path.Combine(root, "__MACOSX");
        Directory.CreateDirectory(macosx);
        File.WriteAllText(Path.Combine(macosx, "EU_BPR_528-2012.txt"), "mac junk");

        return root;
    }

    [Fact]
    public async Task Imports_body_through_medallion_and_skips_junk()
    {
        var root = BuildFixture();
        try
        {
            var h = new Harness();

            var report = await h.Build().ImportAsync(root, default);

            // Exactly one real doc imported; the metadata + `._junk` files are skipped; __MACOSX ignored.
            Assert.Equal(1, report.Docs);
            Assert.True(report.Chunks > 0);
            Assert.Equal(0, report.Errors);
            Assert.Equal(2, report.Skipped); // the _metadata.txt and the ._junk.txt (__MACOSX ignored at dir level)
            Assert.Contains(report.Results, r => r.DocId == "eu-bpr-528-2012" && r.Result == DocResult.Added);

            // Bronze: raw body + meta sidecar + pdf provenance were written under seed/{region}/{docId}/.
            var docId = "eu-bpr-528-2012";
            Assert.True(h.Bronze.Blobs.ContainsKey($"seed/02_European_Union/{docId}/raw.txt"));
            Assert.True(h.Bronze.Blobs.ContainsKey($"seed/02_European_Union/{docId}/meta.json"));
            Assert.True(h.Bronze.Blobs.ContainsKey($"seed/02_European_Union/{docId}/source.pdf.txt"));
            Assert.Equal("EU_BPR_528-2012.pdf",
                Encoding.UTF8.GetString(h.Bronze.Blobs[$"seed/02_European_Union/{docId}/source.pdf.txt"]));
            // The mac-junk body was NOT ingested (no bronze under __MACOSX).
            Assert.DoesNotContain(h.Bronze.Blobs.Keys, k => k.Contains("__MACOSX"));

            // Silver: chunks are `live` (authoritative seed) and carry the resolved citation.
            var chunks = h.Silver.Items.Values.Where(c => c.DocId == docId).ToList();
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.Equal("live", c.Status));
            var cite = chunks[0].Citation;
            Assert.StartsWith("Regulation (EU) No 528/2012", cite.Regulation); // from Official Title
            Assert.Equal("European Union", cite.Authority);
            Assert.Equal("32012R0528", cite.EntryId);                          // CELEX
            Assert.Equal("2012-05-22", cite.OfficialDate);
            Assert.False(string.IsNullOrEmpty(cite.SourceUrl));
            // Deterministic chunk ids.
            Assert.Equal($"{docId}#0", chunks.OrderBy(c => c.ChunkIndex).First().Id);

            // Gold: embedded + pushed, once per chunk, with EnsureIndex called.
            Assert.Equal(1, h.Search.EnsureCalls);
            Assert.Equal(chunks.Count, h.Search.Pushed.Count);
            Assert.All(h.Search.Pushed, g => Assert.Equal("European Union", g.Authority));

            // State advanced so the monthly sync treats the doc as a known baseline.
            Assert.NotNull(await h.State.GetAsync(docId, "02_European_Union", default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Is_idempotent_rerun_does_not_duplicate()
    {
        var root = BuildFixture();
        try
        {
            var h = new Harness();
            await h.Build().ImportAsync(root, default);
            var silverAfterFirst = h.Silver.Items.Count;

            await h.Build().ImportAsync(root, default); // re-run same folder

            // Deterministic ids → merge, not duplicate.
            Assert.Equal(silverAfterFirst, h.Silver.Items.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Missing_root_folder_reports_an_error_not_a_throw()
    {
        var h = new Harness();
        var report = await h.Build().ImportAsync(@"Z:\does\not\exist-smx", default);
        Assert.Equal(0, report.Docs);
        Assert.Equal(1, report.Errors);
    }
}
