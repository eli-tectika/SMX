using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeCatalogLookup : ICatalogLookup
{
    public Dictionary<string, List<CatalogCard>> Cards { get; } = new();
    public List<string> Calls { get; } = [];
    public Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        Calls.Add(element);
        return Task.FromResult<IReadOnlyList<CatalogCard>>(Cards.TryGetValue(element, out var c) ? c : []);
    }

    /// Register the cards a lookup for <paramref name="element"/> returns. Cost-stage tests pass cards that
    /// carry a price/pack (via CatalogCard's optional trailing params); every other test omits them and the
    /// card's Price/Pack stay null. Returns `this` so a test can chain a couple of elements in one setup line.
    public FakeCatalogLookup Returns(string element, params CatalogCard[] cards)
    {
        Cards[element] = [.. cards];
        return this;
    }
}

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

public sealed class FakeLearnedConclusionsSearch : ILearnedConclusionsSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}
