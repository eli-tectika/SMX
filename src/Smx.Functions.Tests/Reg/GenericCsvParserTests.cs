using System.Text;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class GenericCsvParserTests
{
    private static RegSource SourceWith(IReadOnlyDictionary<string, string>? cfg) => new(
        "echa-svhc", "REACH SVHC Candidate List", "ECHA", "dataset", "echa.europa.eu",
        "GenericCsvParser", true, 1, new[] { new RegDoc("svhc", "https://echa.europa.eu/x.csv", "SVHC") },
        ParserConfig: cfg);

    private const string Csv =
        "Substance name,EC / List number,Date of inclusion,Reason for inclusion\n" +
        "Lead,231-100-4,27/06/2018,Toxic for reproduction\n" +
        "Cadmium,231-152-8,20/06/2011,Carcinogenic\n";

    [Fact]
    public void Config_driven_columns_produce_cited_chunks()
    {
        var cfg = new Dictionary<string, string>
        {
            ["nameColumn"] = "substance",
            ["entryColumn"] = "ec / list",
            ["dateColumn"] = "inclusion",
            ["extraColumns"] = "reason for inclusion",
        };
        var chunks = new GenericCsvParser().Parse(Encoding.UTF8.GetBytes(Csv), SourceWith(cfg), SourceWith(cfg).Documents[0]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("231-100-4", chunks[0].EntryId);
        Assert.Equal("2018-06-27", chunks[0].OfficialDate); // dd/MM/yyyy → ISO
        Assert.Contains("Lead", chunks[0].Text);
        Assert.Contains("Toxic for reproduction", chunks[0].Text);
    }

    [Fact]
    public void Missing_config_yields_zero_chunks_a_parse_anomaly()
    {
        var chunks = new GenericCsvParser().Parse(Encoding.UTF8.GetBytes(Csv), SourceWith(null), SourceWith(null).Documents[0]);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Unrecognised_required_column_yields_zero_chunks()
    {
        var cfg = new Dictionary<string, string> { ["nameColumn"] = "nonexistent", ["entryColumn"] = "ec / list" };
        var chunks = new GenericCsvParser().Parse(Encoding.UTF8.GetBytes(Csv), SourceWith(cfg), SourceWith(cfg).Documents[0]);
        Assert.Empty(chunks);
    }
}
