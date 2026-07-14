using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Tests.Fakes;

public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly Dictionary<string, int> _months = [];

    public int CountFor(string month) => _months.GetValueOrDefault(month);

    public Task<int> ReadAsync(string month, CancellationToken ct) =>
        Task.FromResult(_months.GetValueOrDefault(month));

    public Task<int> AddAsync(string month, int delta, CancellationToken ct)
    {
        var next = _months.GetValueOrDefault(month) + delta;
        _months[month] = next;
        return Task.FromResult(next);
    }
}
