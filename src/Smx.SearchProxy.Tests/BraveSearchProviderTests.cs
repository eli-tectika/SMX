using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Providers;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class BraveSearchProviderTests
{
    /// Records every outgoing request and replies from a queued script.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _script;
        public readonly List<HttpRequestMessage> Requests = [];
        public StubHandler(params HttpResponseMessage[] responses) => _script = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private const string BraveJson = """
    {
      "web": {
        "results": [
          {
            "title": "Yttrium 2-ethylhexanoate",
            "url": "https://pubchem.ncbi.nlm.nih.gov/compound/12345",
            "description": "CAS 80326-98-3, soluble in aliphatic solvents.",
            "age": "2024-03-01",
            "meta_url": { "hostname": "pubchem.ncbi.nlm.nih.gov" }
          }
        ]
      }
    }
    """;

    private static BraveSearchProvider Provider(StubHandler handler, int retries = 3) =>
        new(new HttpClient(handler), new ProxyOptions { ApiKey = "test-key", Retries = retries }, NullLogger<BraveSearchProvider>.Instance);

    [Fact]
    public async Task NormalizesTheSerpJson()
    {
        var handler = new StubHandler(Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);

        Assert.NotNull(hits);
        var hit = Assert.Single(hits);
        Assert.Equal("Yttrium 2-ethylhexanoate", hit.Title);
        Assert.Equal("https://pubchem.ncbi.nlm.nih.gov/compound/12345", hit.Url);
        Assert.Contains("80326-98-3", hit.Snippet);
        Assert.Equal("pubchem.ncbi.nlm.nih.gov", hit.Host);
        Assert.Equal("2024-03-01", hit.Age);
    }

    // Request hygiene (spec §6.4). Anything we send that identifies us or lets Brave correlate our requests
    // is a handle we gave away for free.
    [Fact]
    public async Task SendsOnlyTheApiKeyAndAccept_NoCookieReferrerOrTraceHeader()
    {
        var handler = new StubHandler(Ok(BraveJson));
        await Provider(handler).SearchAsync("yttrium forms", 10, null, default);

        var req = Assert.Single(handler.Requests);
        Assert.True(req.Headers.Contains("X-Subscription-Token"));
        Assert.False(req.Headers.Contains("Cookie"));
        Assert.False(req.Headers.Contains("Referer"));
        Assert.False(req.Headers.Contains("traceparent"));
        Assert.False(req.Headers.Contains("Request-Id"));
        Assert.Null(req.Headers.UserAgent.FirstOrDefault());
    }

    [Fact]
    public async Task Retries5xxThenSucceeds()
    {
        var handler = new StubHandler(Status(HttpStatusCode.BadGateway), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.NotNull(hits);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries429()
    {
        var handler = new StubHandler(Status(HttpStatusCode.TooManyRequests), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.NotNull(hits);
        Assert.Equal(2, handler.Requests.Count);
    }

    // A 401 (bad key) or a 400 (bad query) will never succeed on retry. Retrying just burns quota.
    [Fact]
    public async Task DoesNotRetry4xx()
    {
        var handler = new StubHandler(Status(HttpStatusCode.Unauthorized), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.Null(hits);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExhaustedRetries_ReturnsNullRatherThanThrowing()
    {
        var handler = new StubHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.Null(hits);
    }

    // Invariant 1: exactly one upstream host, ever.
    [Fact]
    public void TargetsOnlyTheBraveApiHost()
    {
        Assert.Equal("api.search.brave.com", BraveSearchProvider.ApiHost);
    }

    [Fact]
    public async Task EmptySerp_ReturnsEmptyList_NotNull()
    {
        var hits = await Provider(new StubHandler(Ok("""{"web":{"results":[]}}"""))).SearchAsync("x", 10, null, default);
        Assert.NotNull(hits);
        Assert.Empty(hits);
    }
}
