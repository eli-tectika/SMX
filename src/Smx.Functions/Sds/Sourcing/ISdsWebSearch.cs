namespace Smx.Functions.Sds.Sourcing;

public sealed record WebHit(Uri Url, string Title);

/// SDS URL discovery. One method, so a fake is a lambda.
///
/// This is deliberately NOT routed through the Search Proxy. The proxy's k-anonymity exists to hide which
/// chemistry a live client project is evaluating; a query keyed by a CAS number from a public catalog
/// carries no project identity, so cover batches would quadruple volume against a 5,000/month cap and put
/// a second Function App in the critical path of every fetch, to protect information the request does not
/// contain. See D4 in the 2026-07-29 spec.
///
/// An implementation MUST NOT throw. A search outage degrades discovery — the curated strategies still
/// have work to do — and must never abort the fetch that asked for it.
public interface ISdsWebSearch
{
    Task<IReadOnlyList<WebHit>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
