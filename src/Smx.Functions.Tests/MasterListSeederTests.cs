using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Seeding;
using Xunit;

public class MasterListSeederTests
{
    [Fact]
    public void Derive_maps_valid_records_and_skips_invalid_and_duplicates()
    {
        var json = """
        [
          { "element":"Sc","compound":"TMHD complex","cas":"15492-49-6" },
          { "element":"Sc","compound":"TMHD complex","cas":"307532-33-8" },
          { "element":"La","compound":"octoate","cas":"n/a" },
          { "element":"","compound":"oxide","cas":"1314-37-0" },
          { "element":"Yb","compound":"Neodecanoate","cas":"27253-31-2" }
        ]
        """;

        var (entries, skipped) = MasterListSeeder.Derive(json);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Element == "Sc" && e.Cas == "15492-49-6"); // first wins
        Assert.Contains(entries, e => e.Element == "Yb" && e.Form == "Neodecanoate");
        Assert.Equal(3, skipped.Count); // duplicate pair, invalid cas, missing element
        Assert.Contains(skipped, s => s.Contains("307532-33-8"));   // the losing duplicate is reported
        Assert.Contains(skipped, s => s.Contains("n/a"));
    }

    [Fact]
    public async Task Seed_is_idempotent_across_runs()
    {
        var store = new InMemoryMasterListStore();
        var seeder = new MasterListSeeder(new MasterListRepo(store));
        var json = """
        [
          { "element":"Yb","compound":"neodecanoate","cas":"27253-31-2" },
          { "element":"Sc","compound":"TMHD complex","cas":"15492-49-6" }
        ]
        """;

        var r1 = await seeder.SeedAsync(json, "2026-07-16T00:00:00Z", default);
        Assert.Equal(2, r1.Derived);
        Assert.Equal(2, r1.Added);
        Assert.Equal(0, r1.AlreadyPresent);

        var r2 = await seeder.SeedAsync(json, "2026-07-16T00:00:00Z", default);
        Assert.Equal(0, r2.Added);
        Assert.Equal(2, r2.AlreadyPresent);
        Assert.Equal(2, store.Items.Count);
        Assert.All(store.Items.Values, e => Assert.Equal(SdsStatus.Pending, e.Status));
        Assert.All(store.Items.Values, e => Assert.Equal("operator", e.AddedBy));
    }

    [Fact]
    public void Derive_handles_the_real_bundled_catalog()
    {
        var json = File.ReadAllText("Resources/catalog-products.json");
        var (entries, skipped) = MasterListSeeder.Derive(json);

        Assert.True(entries.Count >= 40, $"expected a substantial manifest, got {entries.Count}");
        // ids must be distinct (one row per (element, form))
        Assert.Equal(entries.Count, entries.Select(e => DedupKey.ForMasterList(e.Element, e.Form)).Distinct().Count());
        Assert.All(entries, e => Assert.Matches(@"^\d{2,7}-\d{2}-\d$", e.Cas));
        Assert.NotEmpty(skipped); // the catalog is known to contain n/a CAS rows + multi-CAS variants
    }
}
