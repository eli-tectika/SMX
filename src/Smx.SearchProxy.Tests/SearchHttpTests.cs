using Microsoft.Extensions.Logging.Abstractions;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;
using Smx.SearchProxy.Tests.Fakes;
using Smx.SearchProxy.Triggers;
using Xunit;

namespace Smx.SearchProxy.Tests;

/// The trigger's testable core (the HttpRequestData shell around it is a shell — it binds JSON and writes
/// the status code, and nothing else).
public class SearchHttpTests
{
    /// The BlobQuotaStore's real failure mode: five contended ETag CAS attempts, then it gives up rather than
    /// let an uncounted batch egress.
    private sealed class ContendedQuotaStore : IQuotaStore
    {
        public Task<int> ReadAsync(string month, CancellationToken ct) => Task.FromResult(0);
        public Task<int> AddAsync(string month, int delta, CancellationToken ct) =>
            throw new QuotaUnavailableException($"quota store contention for {month}");
    }

    private sealed class RecordingProvider : ISearchProvider
    {
        public readonly List<string> Queries = [];
        public Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct)
        {
            lock (Queries) Queries.Add(query);
            return Task.FromResult<IReadOnlyList<SearchHit>?>([new SearchHit("t", "https://example.org/a", "s", "example.org", null)]);
        }
    }

    private static (SearchHttp Http, RecordingProvider Provider) Build(IQuotaStore quota)
    {
        var opts = new ProxyOptions { CoverCount = 4, MonthlyQueryCap = 10_000, RateLimitPerMinute = 1000, ApiKey = "k" };
        var corpus = CoverCorpus.FromJson(
            "{" + string.Join(",", SearchIntents.All.Select(i =>
                $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");
        var provider = new RecordingProvider();

        var pipeline = new SearchPipeline(
            new StructuralGuard(opts),
            new QuotaGuard(quota, opts),
            new InMemorySearchCache(168),
            new CoverBatch(corpus, opts, new RandomShuffler()),
            provider,
            new EgressAudit(NullLogger<EgressAudit>.Instance),
            opts,
            NullLogger<SearchPipeline>.Instance);

        return (new SearchHttp(pipeline, NullLogger<SearchHttp>.Instance), provider);
    }

    private const string Now = "2026-07-13T10:00:00Z";
    private static SearchRequest Req() => new("ytterbium neodecanoate solubility", SearchIntents.CandidateForms, 10);

    // The quota store fails CLOSED — it throws rather than let a batch egress it could not count. Un-caught
    // that is a 500 ("the proxy is broken"), which is both wrong and useless to the agent. It is a 429: the
    // proxy is refusing to spend a budget it cannot confirm.
    [Fact]
    public async Task QuotaStoreFailure_Is429_NotA500()
    {
        var (http, _) = Build(new ContendedQuotaStore());

        var result = await http.ExecuteAsync(Req(), Now, default);

        Assert.Equal(429, result.StatusCode);
        Assert.Equal("quota_unavailable", result.Reason);
        Assert.Null(result.Response);
    }

    // Fail-closed means exactly that: not one query left the building.
    [Fact]
    public async Task QuotaStoreFailure_EgressesNothing()
    {
        var (http, provider) = Build(new ContendedQuotaStore());

        await http.ExecuteAsync(Req(), Now, default);

        Assert.Empty(provider.Queries);
    }

    [Fact]
    public void QuotaStoreFailure_TellsTheAgentWhatToDoInstead()
    {
        var message = SearchHttp.Explain("quota_unavailable");

        Assert.Contains("catalog", message);
    }

    [Fact]
    public async Task HealthyQuota_StillPassesThrough()
    {
        var (http, provider) = Build(new InMemoryQuotaStore());

        var result = await http.ExecuteAsync(Req(), Now, default);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(4, provider.Queries.Count);
    }
}
