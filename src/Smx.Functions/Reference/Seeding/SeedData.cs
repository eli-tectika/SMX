// src/Smx.Functions/Reference/Seeding/SeedData.cs
using Smx.Functions.Reference.Domain;

namespace Smx.Functions.Reference.Seeding;

public sealed record SeedData(
    IReadOnlyList<CompatibilityDoc> Compatibility,
    IReadOnlyList<BibliographyDoc> Bibliography,
    IReadOnlyList<SupplierDoc> Suppliers,
    IReadOnlyList<CatalogDoc> Catalog,
    IReadOnlyList<ReferenceChunkSeed> Chunks)
{
    public static SeedData Empty() => new(
        Array.Empty<CompatibilityDoc>(), Array.Empty<BibliographyDoc>(),
        Array.Empty<SupplierDoc>(), Array.Empty<CatalogDoc>(), Array.Empty<ReferenceChunkSeed>());
}

public sealed record SeedReport(
    int Compatibility, int Bibliography, int Suppliers, int Catalog, int Chunks);
