using Microsoft.Extensions.Configuration;

namespace Smx.SearchProxy.Config;

public sealed class ProxyOptions
{
    public string Provider { get; init; } = "brave";
    public string ApiKey { get; init; } = "";
    public bool DryRun { get; init; }

    /// Real query + (CoverCount - 1) decoys. Clamped to >= 2 in From(): see CoverCountRaw.
    public int CoverCount { get; init; } = 4;
    /// What the operator actually configured, before clamping — Program.cs warns when they differ, so a
    /// misconfiguration is visible in the logs rather than silently corrected.
    public int CoverCountRaw { get; init; } = 4;
    public string CoverCorpusPath { get; init; } = "Config/cover-corpus.json";

    public int MaxQueryChars { get; init; } = 256;
    public int MaxResults { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 15;
    public int Retries { get; init; } = 3;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public int CacheTtlHours { get; init; } = 168;
    public string CacheContainer { get; init; } = "search-cache";
    public string StorageAccount { get; init; } = "";

    public int MonthlyQueryCap { get; init; } = 5000;
    public int RateLimitPerMinute { get; init; } = 30;

    public string? UamiClientId { get; init; }

    public static ProxyOptions From(IConfiguration c)
    {
        var coverRaw = int.TryParse(c["PROXY_COVER_COUNT"], out var cc) ? cc : 4;
        return new ProxyOptions
        {
            Provider = c["PROXY_PROVIDER"] ?? "brave",
            ApiKey = c["PROXY_SEARCH_API_KEY"] ?? "",
            DryRun = bool.TryParse(c["PROXY_DRY_RUN"], out var dr) && dr,
            CoverCountRaw = coverRaw,
            CoverCount = Math.Max(2, coverRaw),
            CoverCorpusPath = c["PROXY_COVER_CORPUS_PATH"] ?? "Config/cover-corpus.json",
            MaxQueryChars = int.TryParse(c["PROXY_MAX_QUERY_CHARS"], out var q) ? q : 256,
            MaxResults = int.TryParse(c["PROXY_MAX_RESULTS"], out var m) ? m : 10,
            TimeoutSeconds = int.TryParse(c["PROXY_TIMEOUT_SECONDS"], out var t) ? t : 15,
            Retries = int.TryParse(c["PROXY_RETRIES"], out var r) ? r : 3,
            CacheTtlHours = int.TryParse(c["PROXY_CACHE_TTL_HOURS"], out var ttl) ? ttl : 168,
            CacheContainer = c["PROXY_CACHE_CONTAINER"] ?? "search-cache",
            StorageAccount = c["AzureWebJobsStorage__accountName"] ?? "",
            MonthlyQueryCap = int.TryParse(c["PROXY_MONTHLY_QUERY_CAP"], out var cap) ? cap : 5000,
            RateLimitPerMinute = int.TryParse(c["PROXY_RATE_LIMIT_PER_MINUTE"], out var rl) ? rl : 30,
            UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        };
    }
}
