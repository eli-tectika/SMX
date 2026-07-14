using Microsoft.Extensions.Logging.Abstractions;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;
using Smx.SearchProxy.Tests.Fakes;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class SearchPipelineTests
{
    /// Records every query it is asked for, so the tests can assert on what would actually have egressed.
    private sealed class RecordingProvider(Func<string, IReadOnlyList<SearchHit>?>? responder = null) : ISearchProvider
    {
        public readonly List<string> Queries = [];
        public Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct)
        {
            lock (Queries) Queries.Add(query);
            var r = responder ?? (q => new[] { new SearchHit($"title {q}", $"https://example.org/{Uri.EscapeDataString(q)}", "snippet", "example.org", null) });
            return Task.FromResult(r(query));
        }
    }

    private sealed class Harness
    {
        public readonly RecordingProvider Provider;
        public readonly InMemorySearchCache Cache = new(168);
        public readonly InMemoryQuotaStore Quota = new();
        public readonly SearchPipeline Pipeline;

        public Harness(int coverCount = 4, int monthlyCap = 10_000, RecordingProvider? provider = null)
        {
            Provider = provider ?? new RecordingProvider();
            var opts = new ProxyOptions
            {
                CoverCount = coverCount, MonthlyQueryCap = monthlyCap, RateLimitPerMinute = 1000, ApiKey = "k",
            };
            var corpus = CoverCorpus.FromJson(
                "{" + string.Join(",", SearchIntents.All.Select(i =>
                    $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");

            Pipeline = new SearchPipeline(
                new StructuralGuard(opts),
                new QuotaGuard(Quota, opts),
                Cache,
                new CoverBatch(corpus, opts, new RandomShuffler()),
                Provider,
                new EgressAudit(NullLogger<EgressAudit>.Instance),
                opts,
                NullLogger<SearchPipeline>.Instance);
        }
    }

    private const string Now = "2026-07-13T10:00:00Z";
    private static SearchRequest Req(string q = "ytterbium neodecanoate solubility") => new(q, SearchIntents.CandidateForms, 10);

    [Fact]
    public async Task HappyPath_ReturnsOnlyTheRealQuerysResults()
    {
        var h = new Harness();
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Response);
        Assert.False(result.Response!.CacheHit);
        Assert.Equal(4, result.Response.CoverCount);
        // The decoys' results must NOT leak into the response — the caller sees only what it asked for.
        Assert.All(result.Response.Results, hit => Assert.Contains("ytterbium", hit.Title));
    }

    // Invariant 4. This is the test that the anonymization is actually happening.
    [Fact]
    public async Task TheRealQueryEgressesInsideABatchOfDecoys()
    {
        var h = new Harness(coverCount: 4);
        await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(4, h.Provider.Queries.Count);
        Assert.Single(h.Provider.Queries, q => q == "ytterbium neodecanoate solubility");
        Assert.Equal(3, h.Provider.Queries.Count(q => q.StartsWith(SearchIntents.CandidateForms)));
    }

    // The decoys are not waste: their results are cached, so the NEXT real query that happens to match one
    // never egresses at all.
    [Fact]
    public async Task DecoyResultsAreCached()
    {
        var h = new Harness(coverCount: 4);
        await h.Pipeline.RunAsync(Req(), Now, default);
        Assert.Equal(4, h.Cache.Writes);
    }

    [Fact]
    public async Task CacheHit_EgressesNothing()
    {
        var h = new Harness();
        await h.Pipeline.RunAsync(Req(), Now, default);
        var before = h.Provider.Queries.Count;

        var second = await h.Pipeline.RunAsync(Req(), "2026-07-13T11:00:00Z", default);

        Assert.True(second.Response!.CacheHit);
        Assert.Equal(0, second.Response.CoverCount);
        Assert.Equal(before, h.Provider.Queries.Count); // not one more call
    }

    [Fact]
    public async Task BlockedQuery_Is400_AndNeverEgresses()
    {
        var h = new Harness();
        var result = await h.Pipeline.RunAsync(
            new SearchRequest("marker for 3f2504e0-4f89-11d3-9a0c-0305e82c3301", SearchIntents.CandidateForms), Now, default);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("contains_guid", result.Reason);
        Assert.Empty(h.Provider.Queries);
    }

    [Fact]
    public async Task QuotaExceeded_Is429_AndNeverEgresses()
    {
        var h = new Harness(coverCount: 4, monthlyCap: 2);
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(429, result.StatusCode);
        Assert.Equal("quota_exceeded", result.Reason);
        Assert.Empty(h.Provider.Queries);
    }

    // Absence of evidence is not evidence of absence: a provider failure must NOT look like "no results".
    // An agent that reads an empty list as "nothing exists" would draw a false conclusion.
    [Fact]
    public async Task ProviderFailureOnTheRealQuery_Is502_NotAnEmptyResultSet()
    {
        var h = new Harness(provider: new RecordingProvider(q => q.StartsWith("ytterbium") ? null : []));
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.Response);
    }

    // A decoy failing is irrelevant — nobody consumes its results. It must not fail the real query.
    [Fact]
    public async Task DecoyFailure_DoesNotFailTheRealQuery()
    {
        var h = new Harness(provider: new RecordingProvider(q =>
            q.StartsWith(SearchIntents.CandidateForms) ? null : [new SearchHit("t", "https://example.org/a", "s", "example.org", null)]));
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.Response!.Results);
    }

    [Fact]
    public async Task ProviderNotConfigured_Is503()
    {
        var opts = new ProxyOptions { ApiKey = "", DryRun = false, CoverCount = 4, RateLimitPerMinute = 100, MonthlyQueryCap = 100 };
        var corpus = CoverCorpus.FromJson(
            "{" + string.Join(",", SearchIntents.All.Select(i =>
                $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} d{n}\"")) + "]")) + "}");
        var pipeline = new SearchPipeline(
            new StructuralGuard(opts), new QuotaGuard(new InMemoryQuotaStore(), opts), new InMemorySearchCache(168),
            new CoverBatch(corpus, opts, new RandomShuffler()), new RecordingProvider(),
            new EgressAudit(NullLogger<EgressAudit>.Instance), opts, NullLogger<SearchPipeline>.Instance);

        var result = await pipeline.RunAsync(Req(), Now, default);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("provider_not_configured", result.Reason);
    }
}
