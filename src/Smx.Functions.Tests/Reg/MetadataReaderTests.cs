using Smx.Functions.Reg.Seeding;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class MetadataReaderTests
{
    // Mirrors the real EU `_metadata.txt` shape: mostly `Key: Value`, but Official Title's value is on the next line.
    private const string MetadataFile = """
    REGULATION METADATA
    ===================

    Official Title:
    Regulation (EC) No 1272/2008 of the European Parliament and of the Council of 16 December 2008 on classification, labelling and packaging of substances and mixtures

    Document Number: 32008R1272
    CELEX Number: 32008R1272
    ELI URI: http://data.europa.eu/eli/reg/2008/1272/oj

    Document Date: 2008-12-16
    Publication Date: 2008-12-31

    Source: EUR-Lex (https://eur-lex.europa.eu/eli/reg/2008/1272/oj/eng)
    Scraped: 2026-06-18
    """;

    [Fact]
    public void Metadata_file_path_produces_a_full_citation()
    {
        var md = MetadataReader.ParseMetadata(MetadataFile);
        Assert.StartsWith("Regulation (EC) No 1272/2008", md.OfficialTitle);
        Assert.Equal("32008R1272", md.Celex);
        Assert.Equal("2008-12-16", md.DocumentDate);
        Assert.Equal("https://eur-lex.europa.eu/eli/reg/2008/1272/oj/eng", md.SourceUrl);

        var c = MetadataReader.ToCitation("02_European_Union", "CLP_Regulation_1272-2008", md, new BodyHeader(null, null));
        Assert.StartsWith("Regulation (EC) No 1272/2008", c.Regulation);   // from Official Title
        Assert.Equal("European Union", c.Authority);                       // derived from region
        Assert.Equal("32008R1272", c.EntryId);                             // CELEX rides along
        Assert.Equal("https://eur-lex.europa.eu/eli/reg/2008/1272/oj/eng", c.SourceUrl);
        Assert.Equal("2008-12-16", c.OfficialDate);                        // Document Date preferred
    }

    [Fact]
    public void Body_header_fallback_extracts_source_url_and_date()
    {
        var body = """
        SOURCE URL: https://www.basel.int/theconvention/overview/tabid/1271/default.aspx
        ALSO CAPTURED FROM: https://www.basel.int/other

        Basel Convention on the Control of Transboundary Movements of Hazardous Wastes
        ==============================================================================

        Published: February 2025

        OVERVIEW
        """;

        var header = MetadataReader.ParseBody(body);
        Assert.Equal("https://www.basel.int/theconvention/overview/tabid/1271/default.aspx", header.SourceUrl);
        Assert.Equal("February 2025", header.Published);

        // No metadata sidecar → regulation derived from the file name, authority from the region.
        var c = MetadataReader.ToCitation("01_Global", "Basel_Convention_Hazardous_Waste", null, header);
        Assert.Equal("Basel Convention Hazardous Waste", c.Regulation);
        Assert.Equal("Global", c.Authority);
        Assert.Null(c.EntryId);
        Assert.Equal(header.SourceUrl, c.SourceUrl);
        Assert.Equal("February 2025", c.OfficialDate);
    }

    [Fact]
    public void Source_url_with_trailing_annotation_extracts_only_the_url()
    {
        var header = MetadataReader.ParseBody(
            "SOURCE URL: https://dtsc.ca.gov/scp/food (original) / https://leginfo.ca.gov/bill (retrieved)\n");
        Assert.Equal("https://dtsc.ca.gov/scp/food", header.SourceUrl);
    }
}
