using Smx.Functions.Reference.Domain;
using Smx.ReferenceData.Transform;
using Xunit;

public class MappersTests
{
    private static SheetRow Row(params (string, string)[] kv)
        => new(kv.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void CompatibilityRules_maps_verdict_row_with_refs()
    {
        var rows = new[]
        {
            Row(("Dimension","Gold solubility"), ("Element / Pair / Form / Class","Zr"),
                ("Substrate / Application","Gold"), ("Verdict","Caution"),
                ("Reason","Limited Au(fcc) solubility"), ("Key Ref(s)","G15,G26")),
        };
        var docs = Mappers.CompatibilityRules(rows);
        var d = Assert.Single(docs);
        Assert.Equal("rule|Zr|gold-solubility-gold", d.Id);
        Assert.Equal("Zr", d.Element);
        Assert.Equal(ReferenceDocType.Rule, d.DocType);
        Assert.Equal("Caution", d.Verdict);
        Assert.Equal(new[] { "G15", "G26" }, d.RefIds);
    }

    [Fact]
    public void Bibliography_maps_refId_as_partition_and_id()
    {
        var rows = new[]
        {
            Row(("Ref ID","G15"), ("Title / Citation","Au-Zr system"), ("Source","JPE"),
                ("Year","1985"), ("Type","Phase-diagram"), ("DOI / URL / Identifier","10.1007/x"),
                ("Dimension","Substrate solubility"), ("Substrate","Gold"), ("Elements","Zr"),
                ("What it establishes","terminal fcc"), ("Verification","verified-fetched")),
        };
        var docs = Mappers.Bibliography(rows);
        var d = Assert.Single(docs);
        Assert.Equal("G15", d.Id);
        Assert.Equal("G15", d.RefId);
        Assert.Equal(new[] { "Zr" }, d.Elements);
    }

    [Fact]
    public void CatalogProducts_keys_by_element_and_builds_search_chunk_source()
    {
        var rows = new[]
        {
            Row(("Element(s)","Y"), ("Compound / Form","TMHD complex"),
                ("Molecule (full name)","Y(TMHD)3"), ("CAS","15632-39-0"),
                ("Purity","99.9%"), ("Supplier","ProChem"), ("Price","$350"),
                ("Pack / Quantity","10 g"), ("Notes / Source","prochemonline.com")),
        };
        var docs = Mappers.CatalogProducts(rows);
        var d = Assert.Single(docs);
        Assert.Equal("Y", d.Element);
        Assert.Equal(ReferenceDocType.Product, d.DocType);
        Assert.Equal("15632-39-0", d.Cas);
    }

    [Fact]
    public void SearchChunks_only_emits_citable_chunks_and_every_chunk_has_a_handle()
    {
        var rules = new[]
        {
            new CompatibilityDoc("rule|Zr|gold", "Zr", ReferenceDocType.Rule,
                Dimension: "Gold solubility", Substrate: "Gold", Verdict: "Caution",
                Reason: "Limited Au(fcc) solubility", RefIds: new[] { "G15" }),
            new CompatibilityDoc("rule|Xx|none", "Xx", ReferenceDocType.Rule,
                Dimension: "Gold solubility", Verdict: "Go", Reason: "no cited source",
                RefIds: Array.Empty<string>()),   // must be dropped (no citation handle)
        };
        var biblio = new[]
        {
            new BibliographyDoc("G15", "G15", "Au-Zr system", "JPE", "1985", "Phase-diagram",
                "10.1007/x", "Substrate solubility", "Gold", new[] { "Zr" }, "terminal fcc", "verified"),
        };
        var chunks = Mappers.SearchChunks(rules, biblio, "compatibility-2026-07");

        Assert.Equal(2, chunks.Count);   // 1 citable rule + 1 bibliography entry; uncited rule dropped
        Assert.All(chunks, c =>
            Assert.True(c.RefIds.Count > 0 || !string.IsNullOrEmpty(c.Doi) || !string.IsNullOrEmpty(c.Url)));
    }
}
