using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Sds.Sourcing;
using Xunit;

public class BraveSdsWebSearchTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? Last;
        public int Calls;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        { _body = body; _status = status; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                RequestMessage = request,
                Content = new StringContent(_body),
            });
        }
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }

    private static BraveSdsWebSearch Search(HttpMessageHandler handler, int maxResponseBytes = 2 * 1024 * 1024)
        => new(new HttpClient(handler), "key", NullLogger<BraveSdsWebSearch>.Instance, maxResponseBytes);

    private const string BraveJson = """
        {"web":{"results":[
          {"url":"https://a.example/x.pdf","title":"Sodium hydroxide SDS"},
          {"url":"https://b.example/y","title":"Product page"}]}}
        """;

    [Fact]
    public async Task ResultsAreParsedFromTheProviderPayload()
    {
        var hits = await Search(new StubHandler(BraveJson)).SearchAsync("1310-73-2 safety data sheet", 5, default);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://a.example/x.pdf", hits[0].Url.ToString());
        Assert.Equal("Sodium hydroxide SDS", hits[0].Title);
    }

    [Fact]
    public async Task TheQueryGoesUpstreamUnderTheSubscriptionToken()
    {
        var handler = new StubHandler(BraveJson);
        await Search(handler).SearchAsync("1310-73-2 safety data sheet", 5, default);

        Assert.Equal("api.search.brave.com", handler.Last!.RequestUri!.Host);
        Assert.Contains("1310-73-2", Uri.UnescapeDataString(handler.Last.RequestUri.Query));
        Assert.Equal("key", Assert.Single(handler.Last.Headers.GetValues("X-Subscription-Token")));
    }

    // A search outage must degrade discovery, never fail the fetch: curated strategies still have work
    // to do, and an exception here would abort the whole ensure call.
    [Fact]
    public async Task AProviderFailureYieldsNoHitsRatherThanThrowing()
    {
        var search = Search(new StubHandler("", HttpStatusCode.ServiceUnavailable));

        Assert.Empty(await search.SearchAsync("anything", 5, default));
    }

    // Same rule one layer lower: DNS death, a reset connection, a TLS failure. Still not fatal.
    [Fact]
    public async Task ATransportFailureYieldsNoHitsRatherThanThrowing()
    {
        Assert.Empty(await Search(new ExplodingHandler()).SearchAsync("anything", 5, default));
    }

    // Junk on the wire is a failed search, not a crash.
    [Fact]
    public async Task AnUnparseablePayloadYieldsNoHits()
    {
        Assert.Empty(await Search(new StubHandler("<html>not json</html>")).SearchAsync("anything", 5, default));
    }

    [Fact]
    public async Task AnOversizeResponseIsAbandoned()
    {
        Assert.Empty(await Search(new StubHandler(BraveJson), maxResponseBytes: 8).SearchAsync("anything", 5, default));
    }

    // A result with no url is skipped rather than throwing on the Uri parse.
    [Fact]
    public async Task ResultsWithoutAUsableUrlAreSkipped()
    {
        var json = """
            {"web":{"results":[
              {"title":"no url here"},
              {"url":"not a uri","title":"junk"},
              {"url":"https://ok.example/s.pdf","title":"good"}]}}
            """;
        var hits = await Search(new StubHandler(json)).SearchAsync("anything", 5, default);

        Assert.Equal("https://ok.example/s.pdf", Assert.Single(hits).Url.ToString());
    }

    // The dry-run implementation is what a local run and a keyless deploy get: no egress, no hits.
    [Fact]
    public async Task TheDryRunImplementationSearchesNothing()
    {
        var search = new DryRunSdsWebSearch(NullLogger<DryRunSdsWebSearch>.Instance);

        Assert.Empty(await search.SearchAsync("1310-73-2 safety data sheet", 5, default));
    }
}
