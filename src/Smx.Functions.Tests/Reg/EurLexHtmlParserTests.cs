using System.Text;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class EurLexHtmlParserTests
{
    private static RegSource Source(string annex, string? officialDate = null)
    {
        var cfg = new Dictionary<string, string> { ["annex"] = annex };
        if (officialDate is not null) cfg["officialDate"] = officialDate;
        return new RegSource(
            "eu-pops", "EU POPs Regulation (EU) 2019/1021", "EUR-Lex", "document", "publications.europa.eu",
            "EurLexHtmlParser", false, 1,
            new[] { new RegDoc("celex-32019R1021", "http://publications.europa.eu/resource/celex/32019R1021", "POPs") },
            ParserConfig: cfg);
    }

    private static IReadOnlyList<ParsedChunk> Parse(string html, RegSource src)
        => new EurLexHtmlParser().Parse(Encoding.UTF8.GetBytes(html), src, src.Documents[0]);

    // Real CONVEX OJ XHTML trimmed from the POPs base act (CELEX 32019R1021), Annex I Part A: the header row
    // plus the real Chlordane (57-74-9) and Aldrin (309-00-2) substance rows, then the ANNEX II heading that
    // bounds the section. Structure verbatim: <table class="oj-table">, <tr class="oj-table">, header cells
    // <p class="oj-tbl-hdr">, body cells <p class="oj-tbl-txt">.
    private const string PopsAnnexI = """
    <div class="eli-container" id="anx_I">
       <p class="oj-doc-ti" id="d1e32-59-1">ANNEX I</p>
       <p class="oj-ti-grseq-1"><span class="oj-bold">Part A</span></p>
       <table width="100%" border="0" cellspacing="0" cellpadding="0" class="oj-table">
          <col width="28%"/><col width="28%"/><col width="28%"/><col width="17%"/>
          <tbody>
             <tr class="oj-table">
                <td valign="top" class="oj-table"><p class="oj-tbl-hdr">Substance</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-hdr">CAS No</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-hdr">EC No</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-hdr">Specific exemption on intermediate use or other specification</p></td>
             </tr>
             <tr class="oj-table">
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">Chlordane</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">57-74-9</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">200-349-0</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">&#8212;</p></td>
             </tr>
             <tr class="oj-table">
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">Aldrin</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">309-00-2</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">206-215-8</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">&#8212;</p></td>
             </tr>
          </tbody>
       </table>
    </div>
    <div class="eli-container" id="anx_II">
       <p class="oj-doc-ti" id="d1e32-66-1">ANNEX II</p>
    </div>
    """;

    // Real oj-table trimmed from the RoHS base act (CELEX 32011L0065), Annex III exemptions: a genuine
    // <table class="oj-table"> whose rows carry exemption numbers/prose but NO CAS/EC identifier — every row
    // must be skipped (a parse anomaly), demonstrating the identifier gate on a real table.
    private const string RohsAnnexIII = """
    <div class="eli-container" id="anx_III">
       <p class="oj-doc-ti" id="d1e32-101-1">ANNEX III</p>
       <table width="100%" border="0" cellspacing="0" cellpadding="0" class="oj-table">
          <col width="20%"/><col width="50%"/><col width="30%"/>
          <tbody>
             <tr class="oj-table">
                <td valign="top" class="oj-table" colspan="2"><p class="oj-tbl-hdr">Exemption</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-hdr">Scope and dates of applicability</p></td>
             </tr>
             <tr class="oj-table">
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">1</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">Mercury in single capped (compact) fluorescent lamps not exceeding (per burner):</p></td>
                <td valign="top" class="oj-table"><p class="oj-normal"> </p></td>
             </tr>
             <tr class="oj-table">
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">1(a)</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">For general lighting purposes &lt; 30 W: 5 mg</p></td>
                <td valign="top" class="oj-table"><p class="oj-tbl-txt">Expires on 31 December 2011</p></td>
             </tr>
          </tbody>
       </table>
    </div>
    """;

    [Fact]
    public void Emits_one_chunk_per_substance_row_with_cas_entry_id()
    {
        var chunks = Parse(PopsAnnexI, Source("I", "2019-06-25"));

        // Header row skipped (no oj-tbl-txt / no identifier); two substance rows emitted.
        Assert.Equal(2, chunks.Count);

        var aldrin = Assert.Single(chunks, c => c.EntryId == "309-00-2");
        Assert.Contains("Aldrin", aldrin.Text);
        Assert.Contains("309-00-2 | 206-215-8", aldrin.Text); // real cell texts joined with " | "
        Assert.Equal("Annex I", aldrin.ArticleOrAnnex);
        Assert.Equal("2019-06-25", aldrin.OfficialDate); // taken from parserConfig, not fabricated

        var chlordane = Assert.Single(chunks, c => c.EntryId == "57-74-9");
        Assert.Contains("Chlordane", chlordane.Text);
    }

    [Fact]
    public void OfficialDate_is_empty_when_not_configured()
    {
        var chunks = Parse(PopsAnnexI, Source("I"));
        Assert.All(chunks, c => Assert.Equal("", c.OfficialDate));
    }

    [Fact]
    public void Wrong_annex_yields_zero_chunks_a_parse_anomaly()
    {
        // The POPs fixture has no Annex XVII; the parser must not fall back to a guessed table.
        Assert.Empty(Parse(PopsAnnexI, Source("XVII")));
    }

    [Fact]
    public void Junk_html_yields_zero_chunks()
    {
        Assert.Empty(Parse("<html><body><p>not an annex</p></body></html>", Source("I")));
        Assert.Empty(Parse("", Source("I")));
    }

    [Fact]
    public void Missing_annex_config_yields_zero_chunks()
    {
        var src = new RegSource(
            "eu-pops", "EU POPs", "EUR-Lex", "document", "publications.europa.eu",
            "EurLexHtmlParser", false, 1, new[] { new RegDoc("d", "http://x/", "POPs") });
        Assert.Empty(new EurLexHtmlParser().Parse(Encoding.UTF8.GetBytes(PopsAnnexI), src, src.Documents[0]));
    }

    [Fact]
    public void Real_oj_table_rows_without_an_identifier_are_all_skipped()
    {
        // RoHS Annex III is a real oj-table, but none of its exemption rows carry a CAS/EC number → 0 chunks.
        // This is exactly why RoHS stays enabled:false — the base act omits the machine-readable substance CAS.
        Assert.Empty(Parse(RohsAnnexIII, Source("III")));
    }

    [Fact]
    public void Falls_back_to_ec_number_when_no_cas_is_present()
    {
        // Minimal oj-table row with only an EC number (231-100-4) — EntryId must fall back to the EC id.
        const string html = """
        <p class="oj-doc-ti">ANNEX VI</p>
        <table class="oj-table"><tbody>
           <tr class="oj-table">
              <td class="oj-table"><p class="oj-tbl-txt">Iron</p></td>
              <td class="oj-table"><p class="oj-tbl-txt">231-100-4</p></td>
           </tr>
        </tbody></table>
        """;
        var chunk = Assert.Single(Parse(html, Source("VI")));
        Assert.Equal("231-100-4", chunk.EntryId);
        Assert.Contains("Iron", chunk.Text);
    }
}
