namespace Smx.SearchProxy.Pipeline;

/// The monthly provider-call counter. Persistent because Flex Consumption recycles instances and a counter
/// that resets on cold start is not a cap.
public interface IQuotaStore
{
    Task<int> ReadAsync(string month, CancellationToken ct);
    /// Returns the new total.
    /// Throws <see cref="QuotaUnavailableException"/> when it cannot durably record the spend: an
    /// implementation must never return a total it did not persist, because that would uncap the budget.
    Task<int> AddAsync(string month, int delta, CancellationToken ct);
}

/// The store could not confirm the spend, so the pipeline must NOT proceed — we fail closed (see
/// BlobQuotaStore.AddAsync).
///
/// It is its own type, rather than a bare InvalidOperationException, so SearchHttp can map exactly this to a
/// 429 without also swallowing a genuine bug from somewhere else in the pipeline and reporting it to the
/// agent as "budget unavailable". It still derives from InvalidOperationException: an un-caught quota failure
/// remains the same kind of fault it always was.
public sealed class QuotaUnavailableException(string message) : InvalidOperationException(message);
