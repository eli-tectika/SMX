using Microsoft.Extensions.Configuration;

namespace Smx.Functions.Sds.Config;

public sealed class SdsOptions
{
    public string CosmosEndpoint { get; init; } = "";
    public string CosmosDatabase { get; init; } = "smx";
    public string MasterContainer { get; init; } = "sds-master-list";
    public string RegistryContainer { get; init; } = "sds-registry";
    public string SuppliersContainer { get; init; } = "sds-suppliers";
    public string BronzeAccount { get; init; } = "";
    public string BronzeFilesystem { get; init; } = "bronze";
    public string SearchEndpoint { get; init; } = "";
    public string SearchIndex { get; init; } = "sds-index";
    public string FoundryEndpoint { get; init; } = "";
    public string EmbeddingDeployment { get; init; } = "text-embedding-3-large";
    public string? UamiClientId { get; init; }
    public int FetchTimeoutSeconds { get; init; } = 30;
    // How many due entries the sweep processes at once. Bounded on purpose: unbounded fan-out would
    // open a socket per due entry and hit every supplier as a burst.
    public int SweepConcurrency { get; init; } = 5;
    // The whole-attempt budget for one on-demand fetch. An agent is waiting on this call.
    public int EnsureBudgetSeconds { get; init; } = 45;
    public int RevisionRecheckDays { get; init; } = 90;
    public bool DryRun { get; init; }
    // Hosts to refuse outright. A denylist, not an allowlist: the default for an unknown host is now
    // ALLOW, and this exists only for hosts we have learned are tarpits or serve junk.
    public IReadOnlyList<string> Denylist { get; init; } = [];
    // Brave key for webDiscovery. Empty is a supported state, not a misconfiguration: without it the
    // dry-run search is wired instead and discovery simply contributes nothing.
    public string SearchApiKey { get; init; } = "";
    public string AllowlistPath { get; init; } = "Sds/Config/suppliers.allowlist.json";
    public string SeedCatalogPath { get; init; } = "Reference/Seed/catalog-products.json";
    public int MaxPdfBytes { get; init; } = 25 * 1024 * 1024;
    public int MinGhsSections { get; init; } = 10;

    public static SdsOptions From(IConfiguration c) => new()
    {
        CosmosEndpoint = c["COSMOS_ACCOUNT_ENDPOINT"] ?? "",
        CosmosDatabase = c["COSMOS_DATABASE"] ?? "smx",
        MasterContainer = c["SDS_MASTER_CONTAINER"] ?? "sds-master-list",
        RegistryContainer = c["SDS_REGISTRY_CONTAINER"] ?? "sds-registry",
        SuppliersContainer = c["SDS_SUPPLIERS_CONTAINER"] ?? "sds-suppliers",
        BronzeAccount = c["BRONZE_ACCOUNT_NAME"] ?? "",
        BronzeFilesystem = c["BRONZE_FILESYSTEM"] ?? "bronze",
        SearchEndpoint = c["SEARCH_ENDPOINT"] ?? "",
        SearchIndex = c["SDS_SEARCH_INDEX"] ?? "sds-index",
        FoundryEndpoint = c["FOUNDRY_ENDPOINT"] ?? "",
        EmbeddingDeployment = c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
        UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        FetchTimeoutSeconds = int.TryParse(c["SDS_FETCH_TIMEOUT_SECONDS"], out var t) ? t : 30,
        SweepConcurrency = int.TryParse(c["SDS_SWEEP_CONCURRENCY"], out var sc) ? sc : 5,
        EnsureBudgetSeconds = int.TryParse(c["SDS_ENSURE_BUDGET_SECONDS"], out var eb) ? eb : 45,
        RevisionRecheckDays = int.TryParse(c["SDS_REVISION_RECHECK_DAYS"], out var d) ? d : 90,
        DryRun = bool.TryParse(c["SDS_DRY_RUN"], out var dr) && dr,
        Denylist = (c["SDS_DENYLIST"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant()).ToList(),
        SearchApiKey = c["SDS_SEARCH_API_KEY"] ?? "",
        AllowlistPath = c["SDS_ALLOWLIST_PATH"] ?? "Sds/Config/suppliers.allowlist.json",
        SeedCatalogPath = c["SDS_SEED_CATALOG_PATH"] ?? "Reference/Seed/catalog-products.json",
    };
}
