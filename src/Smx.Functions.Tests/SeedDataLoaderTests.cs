using Smx.Functions.Reference.Domain;
using Smx.Functions.Reference.Seeding;
using Xunit;

public class SeedDataLoaderTests
{
    [Fact]
    public async Task Loads_all_seed_files_into_typed_lists()
    {
        var data = await SeedDataLoader.LoadAsync("Resources/seed-fixture", default);

        Assert.Single(data.Compatibility);
        Assert.Equal(ReferenceDocType.Rule, data.Compatibility[0].DocType);
        Assert.Equal(new[] { "G15", "G26" }, data.Compatibility[0].RefIds);
        Assert.Single(data.Bibliography);
        Assert.Single(data.Suppliers);
        Assert.Equal(2, data.Catalog.Count);     // products + elements
        Assert.Single(data.Chunks);
    }

    [Fact]
    public async Task Missing_file_yields_empty_list_not_error()
    {
        var data = await SeedDataLoader.LoadAsync("Resources/does-not-exist", default);
        Assert.Empty(data.Compatibility);
        Assert.Empty(data.Chunks);
    }
}
