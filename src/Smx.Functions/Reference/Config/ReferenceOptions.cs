// src/Smx.Functions/Reference/Config/ReferenceOptions.cs
using Microsoft.Extensions.Configuration;

namespace Smx.Functions.Reference.Config;

public sealed class ReferenceOptions
{
    public string CosmosDatabase { get; init; } = "smx";
    public string CompatibilityContainer { get; init; } = "ref-compatibility";
    public string BibliographyContainer { get; init; } = "ref-bibliography";
    public string SuppliersContainer { get; init; } = "ref-suppliers";
    public string CatalogContainer { get; init; } = "ref-catalog";
    public string SearchIndex { get; init; } = "smx-reference";
    public string SeedPath { get; init; } = "Reference/Seed";

    public static ReferenceOptions From(IConfiguration c) => new()
    {
        CosmosDatabase = c["COSMOS_DATABASE"] ?? "smx",
        CompatibilityContainer = c["REF_COMPATIBILITY_CONTAINER"] ?? "ref-compatibility",
        BibliographyContainer = c["REF_BIBLIOGRAPHY_CONTAINER"] ?? "ref-bibliography",
        SuppliersContainer = c["REF_SUPPLIERS_CONTAINER"] ?? "ref-suppliers",
        CatalogContainer = c["REF_CATALOG_CONTAINER"] ?? "ref-catalog",
        SearchIndex = c["REF_SEARCH_INDEX"] ?? "smx-reference",
        SeedPath = c["REF_SEED_PATH"] ?? "Reference/Seed",
    };
}
