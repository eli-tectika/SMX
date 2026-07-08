using System.Text;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class OehhaProp65ParserTests
{
    private static readonly RegSource Source = new(
        "oehha-prop65", "California Proposition 65", "OEHHA", "dataset", "oehha.ca.gov",
        "OehhaProp65Parser", true, 1, new[] { new RegDoc("p65-list", "https://oehha.ca.gov/list.csv", "P65") });

    private const string Csv =
        "Chemical,Type of Toxicity,Listing Mechanism,CAS No.,Date Listed\n" +
        "Lead,\"cancer, developmental\",AB,7439-92-1,10/01/1992\n" +
        "Benzene,cancer,LC,71-43-2,02/27/1987\n";

    [Fact]
    public void Parses_one_chunk_per_chemical_with_citation_fields()
    {
        var chunks = new OehhaProp65Parser().Parse(Encoding.UTF8.GetBytes(Csv), Source, Source.Documents[0]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("7439-92-1", chunks[0].EntryId);
        Assert.Equal("1992-10-01", chunks[0].OfficialDate); // MM/dd/yyyy → ISO
        Assert.Contains("Lead", chunks[0].Text);
        Assert.Contains("CAS 7439-92-1", chunks[0].Text);
        Assert.Equal("71-43-2", chunks[1].EntryId);
    }

    [Fact]
    public void Column_order_is_resolved_by_header_not_position()
    {
        var reordered =
            "CAS No.,Date Listed,Chemical,Type of Toxicity\n" +
            "7439-92-1,10/01/1992,Lead,cancer\n";
        var chunks = new OehhaProp65Parser().Parse(Encoding.UTF8.GetBytes(reordered), Source, Source.Documents[0]);

        Assert.Single(chunks);
        Assert.Equal("7439-92-1", chunks[0].EntryId);
        Assert.Equal("1992-10-01", chunks[0].OfficialDate);
    }

    [Fact]
    public void Unrecognised_header_yields_zero_chunks_a_parse_anomaly()
    {
        var junk = "foo,bar\n1,2\n";
        var chunks = new OehhaProp65Parser().Parse(Encoding.UTF8.GetBytes(junk), Source, Source.Documents[0]);
        Assert.Empty(chunks);
    }
}
