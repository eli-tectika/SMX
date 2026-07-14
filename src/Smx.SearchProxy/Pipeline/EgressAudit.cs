using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// The audit trail that turns "anonymizing" from a claim into something the operator can PROVE.
///
/// One structured event per request, under a single message template, so one KQL query answers "show me
/// everything that left the building":
///
///   traces | where message startswith "SearchProxyAudit"
///         | project timestamp, customDimensions.Decision, customDimensions.Intent,
///                   customDimensions.Query, customDimensions.CoverCount
///
/// The query text is logged deliberately. The audit is worthless without it, and it is safe: this component
/// is project-blind, so its log cannot correlate a query back to a project. Log Analytics is private.
public sealed class EgressAudit(ILogger<EgressAudit> log)
{
    private const string Template =
        "SearchProxyAudit decision={Decision} intent={Intent} query={Query} cover={CoverCount} results={ResultCount} reason={Reason}";

    public void Allowed(SearchRequest req, int coverCount, int resultCount) =>
        log.LogInformation(Template, "allowed", req.Intent, req.Query, coverCount, resultCount, "");

    public void CacheHit(SearchRequest req, int resultCount) =>
        log.LogInformation(Template, "cache_hit", req.Intent, req.Query, 0, resultCount, "");

    /// A blocked query is the MOST interesting line in the log: it is the system catching an attempt — by
    /// our own agent — to send something it should not have.
    public void Blocked(SearchRequest req, string reason) =>
        log.LogWarning(Template, "blocked", req.Intent, req.Query, 0, 0, reason);

    public void ProviderFailed(SearchRequest req, int coverCount) =>
        log.LogError(Template, "provider_failed", req.Intent, req.Query, coverCount, 0, "provider_failed");
}
