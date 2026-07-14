using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeWebSearch : IWebSearch
{
    public readonly List<string> Queries = [];
    public WebSearchResult Result { get; set; } = new([], null);

    public Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult(Result);
    }
}
