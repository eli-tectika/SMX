using Microsoft.Extensions.Configuration;

namespace Smx.Functions.Sds.Config;

public sealed class SdsOptions
{
    public string CosmosEndpoint { get; init; } = "";
    public string CosmosDatabase { get; init; } = "smx";
    public string MasterContainer { get; init; } = "sds-master-list";
    public string RegistryContainer { get; init; } = "sds-registry";
    public string BronzeAccount { get; init; } = "";
    public string BronzeFilesystem { get; init; } = "bronze";
    public string SearchEndpoint { get; init; } = "";
    public string SearchIndex { get; init; } = "sds-index";
    public string FoundryEndpoint { get; init; } = "";
    public string EmbeddingDeployment { get; init; } = "text-embedding-3-large";
    public string? UamiClientId { get; init; }
    public int RetryCap { get; init; } = 3;
    public int FetchTimeoutSeconds { get; init; } = 30;
    public int RevisionRecheckDays { get; init; } = 90;
    public bool DryRun { get; init; }
    public string AllowlistPath { get; init; } = "Sds/Config/suppliers.allowlist.json";
    public int MaxPdfBytes { get; init; } = 25 * 1024 * 1024;
    public int MinGhsSections { get; init; } = 10;

    public static SdsOptions From(IConfiguration c) => new()
    {
        CosmosEndpoint = c["COSMOS_ACCOUNT_ENDPOINT"] ?? "",
        CosmosDatabase = c["COSMOS_DATABASE"] ?? "smx",
        MasterContainer = c["SDS_MASTER_CONTAINER"] ?? "sds-master-list",
        RegistryContainer = c["SDS_REGISTRY_CONTAINER"] ?? "sds-registry",
        BronzeAccount = c["BRONZE_ACCOUNT_NAME"] ?? "",
        BronzeFilesystem = c["BRONZE_FILESYSTEM"] ?? "bronze",
        SearchEndpoint = c["SEARCH_ENDPOINT"] ?? "",
        SearchIndex = c["SDS_SEARCH_INDEX"] ?? "sds-index",
        FoundryEndpoint = c["FOUNDRY_ENDPOINT"] ?? "",
        EmbeddingDeployment = c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
        UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        RetryCap = int.TryParse(c["SDS_RETRY_CAP"], out var r) ? r : 3,
        FetchTimeoutSeconds = int.TryParse(c["SDS_FETCH_TIMEOUT_SECONDS"], out var t) ? t : 30,
        RevisionRecheckDays = int.TryParse(c["SDS_REVISION_RECHECK_DAYS"], out var d) ? d : 90,
        DryRun = bool.TryParse(c["SDS_DRY_RUN"], out var dr) && dr,
        AllowlistPath = c["SDS_ALLOWLIST_PATH"] ?? "Sds/Config/suppliers.allowlist.json",
    };
}
