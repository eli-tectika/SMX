using Microsoft.Extensions.Logging;

namespace Smx.Functions.Sds.Sourcing;

/// No egress. Used when SDS_DRY_RUN=true, and when no search key is configured — a missing key is a
/// deployment that cannot search, not a deployment that should crash on first use.
public sealed class DryRunSdsWebSearch(ILogger<DryRunSdsWebSearch> log) : ISdsWebSearch
{
    public Task<IReadOnlyList<WebHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        log.LogInformation("SDS web search (dry run): would have searched {Query}", query);
        return Task.FromResult<IReadOnlyList<WebHit>>([]);
    }
}
