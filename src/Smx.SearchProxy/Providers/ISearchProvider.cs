using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// THE single outbound path of this app. Following the house convention (Sds/Sourcing/IEgressClient,
/// Reg/Sourcing/IRegEgress): a failure is `null`, never an exception — except OperationCanceledException,
/// which is rethrown. An empty list means "the provider answered, and found nothing"; null means "the
/// provider did not answer". The pipeline maps those to 200-with-no-results and 502 respectively, and the
/// difference matters: one is evidence of absence, the other is absence of evidence.
///
/// There is deliberately NO FetchAsync(Uri) here. See spec §3, invariant 2 — the absence of a fetch
/// interface is why third-party hosts never see our IP.
public interface ISearchProvider
{
    Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct);
}
