using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// PROXY_DRY_RUN=true. Zero egress, no API key needed — the whole app, including the cover batch and the
/// cache, runs end to end. Same idiom as DryRunEgressClient / RegDryRunEgress.
public sealed class DryRunSearchProvider(Func<string, IReadOnlyList<SearchHit>?>? responder = null) : ISearchProvider
{
    private readonly Func<string, IReadOnlyList<SearchHit>?> _responder = responder ?? Default;

    public Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct) =>
        Task.FromResult(_responder(query));

    private static IReadOnlyList<SearchHit>? Default(string query) =>
    [
        new SearchHit(
            Title: $"[dry-run] {query}",
            Url: "https://example.invalid/dry-run",
            Snippet: "Dry-run result — PROXY_DRY_RUN=true, nothing left the building.",
            Host: "example.invalid",
            Age: null),
    ];
}
