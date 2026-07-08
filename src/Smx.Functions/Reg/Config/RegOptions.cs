using Microsoft.Extensions.Configuration;

namespace Smx.Functions.Reg.Config;

// Regulatory Sync options. Reuses the shared infra keys (WORKLOAD_UAMI_CLIENT_ID, COSMOS/BRONZE/SEARCH/
// FOUNDRY endpoints) the SDS subsystem already reads, plus REG_* keys for this subsystem's own containers,
// index, schedule, and circuit-breaker thresholds.
public sealed class RegOptions
{
    // Shared infra (same keys as SdsOptions — one app, one identity, one set of endpoints).
    public string CosmosEndpoint { get; init; } = "";
    public string CosmosDatabase { get; init; } = "smx";
    public string BronzeAccount { get; init; } = "";
    public string BronzeFilesystem { get; init; } = "bronze";
    public string SearchEndpoint { get; init; } = "";
    public string FoundryEndpoint { get; init; } = "";
    public string EmbeddingDeployment { get; init; } = "text-embedding-3-large";
    public string? UamiClientId { get; init; }

    // Reg-specific containers (created in Bicep — data-plane identity cannot create them; see SDS doc D3).
    public string StateContainer { get; init; } = "reg-state";
    public string RegistryContainer { get; init; } = "reg-registry";
    public string ReviewContainer { get; init; } = "reg-review";
    public string SilverContainer { get; init; } = "reg-silver";
    public string RunsContainer { get; init; } = "reg-runs";

    // Reg-specific AI Search index (created in code via EnsureIndexAsync — data-plane, allowed).
    public string SearchIndex { get; init; } = "regulatory-corpus";

    // The curated official-source registry seed (git-versioned; loaded into reg-registry).
    public string RegistryPath { get; init; } = "Reg/Config/regulators.registry.json";

    // One-time seed importer: local folder of ~100 pre-collected regulatory documents loaded into the corpus
    // with NO network egress (distinct from the monthly SyncPipeline). Consumed by SeedImportHttp / SeedImporter.
    public string SeedPath { get; init; } = @"C:\SMX\Regulations 2\Regulations";

    // Egress tuning (regulatory datasets/APIs can be larger than a single SDS PDF).
    public int FetchTimeoutSeconds { get; init; } = 60;
    public int MaxDocBytes { get; init; } = 100 * 1024 * 1024;
    public int EgressRetries { get; init; } = 3;
    public bool DryRun { get; init; }

    // A held run that is never signed off is expired after this many days (staged Silver discarded).
    public int HeldExpiryDays { get; init; } = 30;

    // Circuit breaker thresholds. A run is held for human review when a source's diff exceeds either the
    // absolute or the percentage bound (or a parse/format anomaly is detected). In dev these are set very
    // high so a run never holds, enabling fast end-to-end runs. Same code, different config.
    public int AnomalyDiffAbs { get; init; } = 200;
    public int AnomalyDiffPct { get; init; } = 25;

    public static RegOptions From(IConfiguration c) => new()
    {
        CosmosEndpoint = c["COSMOS_ACCOUNT_ENDPOINT"] ?? "",
        CosmosDatabase = c["COSMOS_DATABASE"] ?? "smx",
        BronzeAccount = c["BRONZE_ACCOUNT_NAME"] ?? "",
        BronzeFilesystem = c["BRONZE_FILESYSTEM"] ?? "bronze",
        SearchEndpoint = c["SEARCH_ENDPOINT"] ?? "",
        FoundryEndpoint = c["FOUNDRY_ENDPOINT"] ?? "",
        EmbeddingDeployment = c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
        UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        StateContainer = c["REG_STATE_CONTAINER"] ?? "reg-state",
        RegistryContainer = c["REG_REGISTRY_CONTAINER"] ?? "reg-registry",
        ReviewContainer = c["REG_REVIEW_CONTAINER"] ?? "reg-review",
        SilverContainer = c["REG_SILVER_CONTAINER"] ?? "reg-silver",
        RunsContainer = c["REG_RUNS_CONTAINER"] ?? "reg-runs",
        SearchIndex = c["REG_SEARCH_INDEX"] ?? "regulatory-corpus",
        RegistryPath = c["REG_REGISTRY_PATH"] ?? "Reg/Config/regulators.registry.json",
        SeedPath = c["REG_SEED_PATH"] ?? @"C:\SMX\Regulations 2\Regulations",
        FetchTimeoutSeconds = int.TryParse(c["REG_FETCH_TIMEOUT_SECONDS"], out var t) ? t : 60,
        MaxDocBytes = int.TryParse(c["REG_MAX_DOC_BYTES"], out var m) ? m : 100 * 1024 * 1024,
        EgressRetries = int.TryParse(c["REG_EGRESS_RETRIES"], out var er) ? er : 3,
        DryRun = bool.TryParse(c["REG_DRY_RUN"], out var dr) && dr,
        HeldExpiryDays = int.TryParse(c["REG_HELD_EXPIRY_DAYS"], out var he) ? he : 30,
        AnomalyDiffAbs = int.TryParse(c["REG_ANOMALY_ABS"], out var a) ? a : 200,
        AnomalyDiffPct = int.TryParse(c["REG_ANOMALY_PCT"], out var p) ? p : 25,
    };
}
