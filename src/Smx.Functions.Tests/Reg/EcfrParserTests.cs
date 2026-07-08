using System.Text;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class EcfrParserTests
{
    private static RegSource SourceWith(IReadOnlyDictionary<string, string>? cfg) => new(
        "ecfr-fda-food-contact", "US FDA Food-Contact Regulations (21 CFR)", "FDA", "api", "ecfr.gov",
        "EcfrParser", true, 1, new[] { new RegDoc("t21", "https://www.ecfr.gov/api/versioner/v1/versions/title-21.json", "T21") },
        ParserConfig: cfg);

    // Representative fixture: the eCFR versioner /versions/title-N.json shape (verified against the live API).
    // Includes (a) two historical versions of the same section — must collapse to the latest amendment_date,
    // (b) a section in an out-of-filter part, (c) a removed section, and (d) a normal in-filter section.
    private const string Json = """
    {
      "content_versions": [
        { "date":"2016-12-29","amendment_date":"2016-12-29","issue_date":"2016-12-31","identifier":"170.3",
          "name":"§ 170.3   Definitions.","part":"170","substantive":true,"removed":false,"subpart":"A","title":"21","type":"section" },
        { "date":"2021-04-01","amendment_date":"2021-04-01","issue_date":"2021-04-05","identifier":"170.3",
          "name":"§ 170.3   Definitions.","part":"170","substantive":true,"removed":false,"subpart":"A","title":"21","type":"section" },
        { "date":"2019-01-01","amendment_date":"2019-01-01","issue_date":"2019-01-02","identifier":"178.3297",
          "name":"§ 178.3297   Colorants for polymers.","part":"178","substantive":true,"removed":false,"subpart":"D","title":"21","type":"section" },
        { "date":"2020-06-01","amendment_date":"2020-06-01","issue_date":"2020-06-02","identifier":"573.1",
          "name":"§ 573.1   Definitions.","part":"573","substantive":true,"removed":false,"subpart":"A","title":"21","type":"section" },
        { "date":"2018-03-15","amendment_date":"2018-03-15","issue_date":"2018-03-16","identifier":"177.9999",
          "name":"§ 177.9999   Repealed section.","part":"177","substantive":false,"removed":true,"subpart":"B","title":"21","type":"section" }
      ]
    }
    """;

    [Fact]
    public void Emits_one_chunk_per_current_section_with_latest_amendment_date()
    {
        var cfg = new Dictionary<string, string> { ["parts"] = "170,175,176,177,178" };
        var src = SourceWith(cfg);
        var chunks = new EcfrParser().Parse(Encoding.UTF8.GetBytes(Json), src, src.Documents[0]);

        // 170.3 (collapsed from 2 versions) + 178.3297. 573.1 filtered out (part), 177.9999 dropped (removed).
        Assert.Equal(2, chunks.Count);

        var s1703 = Assert.Single(chunks, c => c.EntryId == "21 CFR 170.3");
        Assert.Equal("2021-04-01", s1703.OfficialDate); // latest amendment_date wins
        Assert.Equal("Part 170", s1703.ArticleOrAnnex);
        Assert.Contains("Definitions", s1703.Text);
        Assert.Contains("last amended 2021-04-01", s1703.Text);
        Assert.DoesNotContain("   ", s1703.Text); // irregular whitespace collapsed

        var s178 = Assert.Single(chunks, c => c.EntryId == "21 CFR 178.3297");
        Assert.Equal("2019-01-01", s178.OfficialDate);
    }

    [Fact]
    public void Removed_and_out_of_filter_sections_are_excluded()
    {
        var cfg = new Dictionary<string, string> { ["parts"] = "170,175,176,177,178" };
        var src = SourceWith(cfg);
        var chunks = new EcfrParser().Parse(Encoding.UTF8.GetBytes(Json), src, src.Documents[0]);

        Assert.DoesNotContain(chunks, c => c.EntryId == "21 CFR 573.1");    // part not in filter
        Assert.DoesNotContain(chunks, c => c.EntryId == "21 CFR 177.9999"); // removed
    }

    [Fact]
    public void No_parts_filter_emits_every_current_section()
    {
        var src = SourceWith(null);
        var chunks = new EcfrParser().Parse(Encoding.UTF8.GetBytes(Json), src, src.Documents[0]);

        // 170.3, 178.3297, 573.1 (removed 177.9999 still dropped). Deterministic order by part number.
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { "21 CFR 170.3", "21 CFR 178.3297", "21 CFR 573.1" },
            chunks.Select(c => c.EntryId).ToArray());
    }

    [Fact]
    public void Missing_content_versions_yields_zero_chunks_a_parse_anomaly()
    {
        var src = SourceWith(null);
        Assert.Empty(new EcfrParser().Parse(Encoding.UTF8.GetBytes("""{"foo":[]}"""), src, src.Documents[0]));
        Assert.Empty(new EcfrParser().Parse(Encoding.UTF8.GetBytes("not json"), src, src.Documents[0]));
    }
}
