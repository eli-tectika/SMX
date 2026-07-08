// src/Smx.Functions/Reference/Seeding/SeedDataLoader.cs
using System.Text.Json;
using Smx.Functions.Reference.Domain;

namespace Smx.Functions.Reference.Seeding;

public static class SeedDataLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<SeedData> LoadAsync(string dir, CancellationToken ct)
    {
        var compat = new List<CompatibilityDoc>();
        compat.AddRange(await ReadAsync<CompatibilityDoc>(dir, "compatibility-rules.json", ct));
        compat.AddRange(await ReadAsync<CompatibilityDoc>(dir, "gold-solubility.json", ct));
        compat.AddRange(await ReadAsync<CompatibilityDoc>(dir, "xrf-lines.json", ct));
        compat.AddRange(await ReadAsync<CompatibilityDoc>(dir, "icp-interference.json", ct));

        var catalog = new List<CatalogDoc>();
        catalog.AddRange(await ReadAsync<CatalogDoc>(dir, "catalog-products.json", ct));
        catalog.AddRange(await ReadAsync<CatalogDoc>(dir, "catalog-elements.json", ct));

        return new SeedData(
            compat,
            await ReadAsync<BibliographyDoc>(dir, "bibliography.json", ct),
            await ReadAsync<SupplierDoc>(dir, "suppliers.json", ct),
            catalog,
            await ReadAsync<ReferenceChunkSeed>(dir, "search-chunks.json", ct));
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string dir, string file, CancellationToken ct)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return Array.Empty<T>();
        await using var s = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(s, Json, ct) ?? new List<T>();
    }
}
