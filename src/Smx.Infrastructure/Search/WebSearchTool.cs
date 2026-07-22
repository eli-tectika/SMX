using Microsoft.Extensions.Logging;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// IWebSearch, with the three controls that must sit INSIDE the VNet, before anything can egress:
///   1. the operator kill switch (WEB_SEARCH_ENABLED),
///   2. the per-project sensitive-term guard — the only layer that knows the client/product names,
///   3. a per-stage query budget, so an agent loop cannot spray egress or burn the provider quota.
///
/// Constructed per stage run (not a singleton) because SensitiveTerms and the budget counter are per project.
///
/// NOT the default path since 2026-07-15: Discovery ships on the model's built-in hosted web search
/// (WEB_SEARCH_PROVIDER=hosted, see ToolBox.HostedWebSearch). This is a FULLY SUPPORTED second mode, kept
/// deliberately for the security posture it buys — set WEB_SEARCH_PROVIDER=proxy to route Discovery's
/// search_web back through this anonymizing egress. It is NOT deprecated and is NOT scheduled for removal:
/// the three controls above are the whole reason it exists, and they apply on this path only.
public sealed class WebSearchTool(
    ISearchProxyClient proxy,
    SensitiveTerms terms,
    bool enabled,
    int maxQueriesPerStage,
    ILogger<WebSearchTool> log) : IWebSearch
{
    private int _used;

    public async Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default)
    {
        if (!enabled)
            return new WebSearchResult([], "external web search is disabled — answer from the catalog and the reference corpus");

        if (!SensitiveTermGuard.IsClean(query, terms, out var offender))
        {
            // The offending term is logged, not returned: the model does not need to be told the client's
            // name to be told it must not use it.
            log.LogWarning("Web search blocked: the query contained the project term {Term}", offender);
            return new WebSearchResult([],
                "that query contained a term that identifies this project (a client, product or project name). " +
                "Rephrase it in generic chemical terms — the external search must never carry anything that identifies this project.");
        }

        if (Interlocked.Increment(ref _used) > maxQueriesPerStage)
            return new WebSearchResult([],
                $"the external-search budget for this stage ({maxQueriesPerStage} queries) is spent — " +
                "continue from the catalog and the reference corpus");

        return await proxy.SearchAsync(query, intent, maxResults: 10, ct);
    }
}
