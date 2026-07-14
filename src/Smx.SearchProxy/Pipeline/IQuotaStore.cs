namespace Smx.SearchProxy.Pipeline;

/// The monthly provider-call counter. Persistent because Flex Consumption recycles instances and a counter
/// that resets on cold start is not a cap.
public interface IQuotaStore
{
    Task<int> ReadAsync(string month, CancellationToken ct);
    /// Returns the new total.
    Task<int> AddAsync(string month, int delta, CancellationToken ct);
}
