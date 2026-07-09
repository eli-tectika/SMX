// src/Smx.Functions.Tests/ReferenceSeederTests.cs
using Smx.Functions.Reference.Config;
using Smx.Functions.Reference.Domain;
using Smx.Functions.Reference.Seeding;
using Xunit;

public class ReferenceSeederTests
{
    private static SeedData Sample() => new(
        Compatibility: new[]
        {
            new CompatibilityDoc("rule|Zr|gold-solubility", "Zr", ReferenceDocType.Rule,
                Dimension: "Gold solubility", Substrate: "Gold", Verdict: "Caution",
                Reason: "Limited Au(fcc) solubility", RefIds: new[] { "G15", "G26" }),
        },
        Bibliography: new[]
        {
            new BibliographyDoc("G15", "G15", "Au-Zr system", "JPE", "1985", "Phase-diagram",
                "10.1007/x", "Substrate solubility", "Gold", new[] { "Zr" }, "terminal fcc", "verified-fetched"),
        },
        Suppliers: new[]
        {
            new SupplierDoc("sigma-aldrich-merck", "Sigma-Aldrich / Merck", "Existing", "RE & inorganic",
                "Germany", null, "sigmaaldrich.com", null, null, null, null, null, new[] { "master" }),
        },
        Catalog: new[]
        {
            new CatalogDoc("product|Y|y-tmhd-3-prochem", "Y", ReferenceDocType.Product,
                Compound: "TMHD complex", Molecule: "Y(TMHD)3", Cas: "15632-39-0", Supplier: "ProChem"),
        },
        Chunks: new[]
        {
            new ReferenceChunkSeed("chunk|rule|Zr|gold-solubility", "Limited Au(fcc) solubility…",
                "Zr", "Gold", "Gold solubility", "Caution", new[] { "G15", "G26" },
                "Au-Zr system", "10.1007/x", null, "Compatibility Rules", "compatibility-2026-07"),
        });

    private static ReferenceSeeder Build(out InMemoryReferenceStore store,
        out FakeReferenceSearchClient search, out FakeEmbedder embedder)
    {
        store = new InMemoryReferenceStore();
        search = new FakeReferenceSearchClient();
        embedder = new FakeEmbedder();
        return new ReferenceSeeder(store, embedder, search, new ReferenceOptions());
    }

    [Fact]
    public async Task Seeds_each_container_and_pushes_embedded_chunks()
    {
        var seeder = Build(out var store, out var search, out _);
        var report = await seeder.SeedAsync(Sample(), default);

        Assert.Equal(1, store.Count("ref-compatibility"));
        Assert.Equal(1, store.Count("ref-bibliography"));
        Assert.Equal(1, store.Count("ref-suppliers"));
        Assert.Equal(1, store.Count("ref-catalog"));
        Assert.Equal(1, search.EnsureCalls);
        Assert.Single(search.Pushed);
        Assert.Equal(3072, search.Pushed[0].ContentVector.Length);   // embedder filled the vector
        Assert.Equal(new[] { "G15", "G26" }, search.Pushed[0].RefIds);
        Assert.Equal(1, report.Chunks);
    }

    [Fact]
    public async Task Reseeding_is_idempotent_no_duplicates()
    {
        var seeder = Build(out var store, out var search, out _);
        await seeder.SeedAsync(Sample(), default);
        await seeder.SeedAsync(Sample(), default);   // second run

        Assert.Equal(1, store.Count("ref-compatibility"));
        Assert.Equal(1, store.Count("ref-bibliography"));
        Assert.Equal(1, store.Count("ref-suppliers"));
        Assert.Equal(1, store.Count("ref-catalog"));
        Assert.Equal(2, search.Pushed.Count);        // pushed both runs, but same id -> merge/upload upsert
        // Search keys must be Azure-AI-Search-safe (letters, digits, _ - =); the natural '|'-laden id is transformed.
        Assert.Matches("^[A-Za-z0-9_=-]+$", search.Pushed[1].Id);
        Assert.StartsWith("chunk-rule-zr-gold-solubility", search.Pushed[1].Id);   // derived from the natural id
        Assert.Equal(search.Pushed[0].Id, search.Pushed[1].Id);   // deterministic across reseeds (idempotent upsert)
    }
}
