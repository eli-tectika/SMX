using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Domain;
using Xunit;

public class SupplierStoreTests
{
    private const string BundledJson = """
    [
      { "supplier":"Alfa", "domain":"fishersci.com", "priority":10, "strategy":"staticMap",
        "sdsUrlTemplate":"https://www.fishersci.com/{productNumber}", "casMap": { "1310-73-2":"AA123" } },
      { "supplier":"Sigma", "domain":"sigmaaldrich.com", "priority":50, "strategy":"productLookup",
        "sdsUrlTemplate":"https://sigmaaldrich.com/{brand}/{productNumber}" },
      { "supplier":"ChemBlink", "domain":"chemblink.com", "priority":90, "strategy":"casTemplate",
        "sdsUrlTemplate":"https://chemblink.com/{cas}.pdf" }
    ]
    """;

    private static AllowlistEntry Operator(string domain, int priority) =>
        new("Operator Added", domain, priority, "casTemplate", $"https://{domain}/{{cas}}.pdf", null, null);

    [Fact]
    public async Task AnEmptyContainerIsSeededFromTheBundledFile()
    {
        var store = new InMemorySupplierStore();

        var provider = await SupplierStore.LoadAsync(store, BundledJson, default);

        Assert.Equal(3, provider.Ordered.Count);
        Assert.Equal(3, (await store.ListAllAsync(default)).Count);   // seeding persisted
    }

    [Fact]
    public async Task StoredSuppliersWinOverTheBundledFile()
    {
        var store = new InMemorySupplierStore();
        await store.UpsertAsync(Operator("new.example", 1), default);

        var provider = await SupplierStore.LoadAsync(store, BundledJson, default);

        Assert.Equal("Operator Added", provider.Ordered[0].Supplier);   // priority 1 sorts first
        // And the bundled file is not merged back in: seeding is a first-run bootstrap, not a floor.
        // If it were a floor, a supplier could never be removed without a redeploy — which is the
        // exact thing this change exists to end.
        Assert.Single(provider.Ordered);
    }

    [Fact]
    public async Task SeedingHappensOnceAndNeverOverwritesLater()
    {
        var store = new InMemorySupplierStore();
        await SupplierStore.LoadAsync(store, BundledJson, default);

        store.Items[Operator("chemblink.com", 5).Id] = Operator("chemblink.com", 5);
        var second = await SupplierStore.LoadAsync(store, BundledJson, default);

        Assert.Equal("Operator Added", second.Ordered.Single(e => e.Domain == "chemblink.com").Supplier);
    }

    // A supplier's identity IS its host, so the operator endpoint updating one cannot silently
    // accumulate duplicate rows racing to be the winner.
    [Fact]
    public async Task ASupplierIsIdentifiedByItsDomain()
    {
        var store = new InMemorySupplierStore();
        await store.UpsertAsync(Operator("New.Example", 1), default);
        await store.UpsertAsync(Operator("new.example", 2), default);

        Assert.Single(await store.ListAllAsync(default));
        Assert.NotNull(await store.GetAsync("NEW.EXAMPLE", default));
    }

    // `id` is derived, not settable — so a payload that carries one (a row read back out of Cosmos, or
    // POSTed straight back to /api/sds/suppliers) must parse rather than throw on the way in.
    [Fact]
    public void AnEntryCarryingADerivedIdStillParses()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<AllowlistEntry>(
            """
            { "id":"new.example", "supplier":"Op", "domain":"New.Example", "priority":1,
              "strategy":"casTemplate", "sdsUrlTemplate":"https://new.example/{cas}.pdf" }
            """,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("new.example", entry!.Id);
        Assert.Equal("casTemplate", entry.Strategy);
    }

    // Local dev has no Cosmos, and Cosmos can have a bad minute. Neither is a reason for the sweep to
    // have no suppliers at all — the bundled file is still on disk.
    [Fact]
    public async Task AnUnreachableStoreFallsBackToTheBundledFile()
    {
        var provider = await SupplierStore.LoadAsync(new UnreachableSupplierStore(), BundledJson, default);

        Assert.Equal(3, provider.Ordered.Count);
    }
}

public class SupplierCatalogTests
{
    // The bundled file, from the content root, exactly as the deployed app reads it.
    private const string BundledPath = "Sds/Config/suppliers.allowlist.json";

    [Fact]
    public async Task TheCatalogReadsTheStoreOnceAndCachesTheResult()
    {
        var store = new InMemorySupplierStore();
        var catalog = new SupplierCatalog(store, BundledPath, null);

        var first = await catalog.GetAsync(default);
        var second = await catalog.GetAsync(default);

        Assert.Same(first, second);
        Assert.Equal(1, store.Lists);
    }

    // Adding a supplier must take effect without a restart, or the Cosmos move has bought nothing
    // over the git-versioned file it replaced.
    [Fact]
    public async Task AnInvalidatedCatalogPicksUpASupplierAddedSinceStartup()
    {
        var store = new InMemorySupplierStore();
        var catalog = new SupplierCatalog(store, BundledPath, null);
        var before = await catalog.GetAsync(default);
        Assert.DoesNotContain(before.Ordered, e => e.Domain == "brand.new");

        await store.UpsertAsync(new AllowlistEntry("Newcomer", "brand.new", 1, "casTemplate",
            "https://brand.new/{cas}.pdf", null, null), default);
        catalog.Invalidate();

        var after = await catalog.GetAsync(default);
        Assert.Equal("Newcomer", after.Ordered[0].Supplier);
    }

    // Concurrent first reads must not each seed the container.
    [Fact]
    public async Task ConcurrentFirstReadsLoadOnce()
    {
        var store = new InMemorySupplierStore();
        var catalog = new SupplierCatalog(store, BundledPath, null);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await catalog.GetAsync(default)));

        Assert.Equal(1, store.Lists);
    }
}
