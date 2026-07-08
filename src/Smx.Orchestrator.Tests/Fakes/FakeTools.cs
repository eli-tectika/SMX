using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeCompatibilityLookup : ICompatibilityLookup
{
    public Dictionary<(string, string), CompatibilityCard> Cards { get; } = new();
    public List<(string Element, string Substrate)> Calls { get; } = [];
    public Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        Calls.Add((element, substrate));
        return Task.FromResult(Cards.TryGetValue((element, substrate), out var c) ? c : (CompatibilityCard?)null);
    }
}

public sealed class FakeSearch : IRegulatorySearch, ISdsSearch, IReferenceSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; set; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}
