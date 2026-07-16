#pragma warning disable CS0618 // exercises the deprecated proxy path (SearchProxyClient) on purpose — kept revivable
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

/// This client decides WHAT THE MODEL IS TOLD WHEN EGRESS FAILS, and that is the whole reason it is tested.
///
/// Its failure mode is not a crash. It is `new WebSearchResult([], null)` — an empty hit list with no note,
/// which ToolBox renders to the Discovery agent as a search that ran fine and found nothing. The agent reads
/// that as "no such marker form exists", excludes a perfectly good candidate, and never mentions the
/// exclusion because as far as it knows nothing went wrong. A dropped connection would silently narrow the
/// candidate set for a live client project. So every failure path below asserts a NOTE comes back, and the
/// notes are asserted verbatim where the proxy wrote them: those strings are addressed to the model.
public class SearchProxyClientTests
{
    /// Answers whatever it is handed, and keeps the request so the project-blindness assertion can read the
    /// body that would actually have gone on the wire.
    private sealed class StubHandler(HttpStatusCode status, string body, Exception? throws = null) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";
        public string? LastAuthorization { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null) LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            if (throws is not null) throw throws;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public TokenRequestContext LastContext { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct)
        {
            LastContext = requestContext;
            return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) =>
            new(GetToken(requestContext, ct));
    }

    private static SearchProxyClient Client(StubHandler handler, FakeCredential? credential = null) =>
        new(new HttpClient(handler), credential ?? new FakeCredential(),
            "https://proxy.internal/", "api://search-proxy", NullLogger<SearchProxyClient>.Instance);

    private const string TwoHits = """
        {
          "results": [
            {"title":"Yttrium 2-ethylhexanoate","url":"https://example.org/y-2eh","snippet":"a soluble form","host":"example.org","age":null},
            {"title":"Yttrium oxide","url":"https://example.org/y2o3","snippet":"insoluble","host":"example.org","age":null}
          ],
          "resultCount": 2, "cacheHit": false, "coverCount": 4
        }
        """;

    [Fact]
    public async Task Ok_MapsResultsToWebHits_WithNoNote()
    {
        var result = await Client(new StubHandler(HttpStatusCode.OK, TwoHits))
            .SearchAsync("yttrium neodecanoate solubility", "discovery.candidate_forms", 10, default);

        Assert.Null(result.Note);                              // nothing went wrong ⇒ nothing to warn the model about
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("Yttrium 2-ethylhexanoate", result.Hits[0].Title);
        Assert.Equal("https://example.org/y-2eh", result.Hits[0].Url);
        Assert.Equal("a soluble form", result.Hits[0].Snippet);
        Assert.Equal("example.org", result.Hits[0].Host);
    }

    /// THE PROJECT-BLINDNESS INVARIANT, client side. The proxy half is enforced by SearchRequest having no
    /// field to hold a project identifier and the trigger deserializing with UnmappedMemberHandling.Disallow.
    /// This is the other half: what this client actually serializes. The crown-jewel secret is not the query
    /// "yttrium neodecanoate solubility" — a hundred labs ask that. It is that ACME'S BOTTLE PROJECT is asking
    /// it. One extra property on the request record, added by someone wiring up "just a correlation id for
    /// debugging", would put that on the wire to a third party inside every cover batch — and the decoys
    /// would go on being sent, so every other test in this suite would stay green.
    [Fact]
    public async Task TheRequestBody_CarriesNoProjectIdentifier_OnlyChemistry()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHits);
        await Client(handler).SearchAsync("yttrium neodecanoate solubility", "discovery.candidate_forms", 10, default);

        using var sent = JsonDocument.Parse(handler.LastRequestBody);
        var keys = sent.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Whitelist, not blacklist: enumerate what MAY be sent, so a newly added property fails this test by
        // default rather than sailing through because nobody thought to blacklist its name.
        Assert.All(keys, k => Assert.Contains(k, new[] { "query", "intent", "maxResults", "freshnessDays" },
            StringComparer.OrdinalIgnoreCase));
        Assert.Contains("query", keys, StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in new[] { "projectId", "client", "product", "correlationId", "componentId" })
            Assert.DoesNotContain(forbidden, keys, StringComparer.OrdinalIgnoreCase);

        // And the same for the VALUES: no client or project name may ride in on the query either.
        Assert.DoesNotContain("acme", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    /// The proxy's messages are WRITTEN FOR THE MODEL, not for a human — SearchHttp.Explain says so in as many
    /// words, and each one tells the agent what to do differently. Relaying them verbatim is the entire point:
    /// "rephrase it in generic chemical terms" is actionable; "search failed" is not; silence is a lie.
    ///
    /// The messages below are copied from SearchHttp.Explain. They are literals rather than a call into it
    /// because Smx.SearchProxy is a Functions app in the OTHER solution and this client must not take a
    /// reference on it — the coupling that matters is the wire contract, which is what is asserted here.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "contains_guid",
        "The query contained an identifier (an id, an address, a URL or a long number). Rephrase it in generic chemical terms — " +
        "the external search must never carry anything that identifies this project.")]
    [InlineData(HttpStatusCode.TooManyRequests, "quota_exceeded",
        "The external-search budget is exhausted. Continue from the catalog and the reference corpus.")]
    [InlineData(HttpStatusCode.BadGateway, "provider_failed",
        "The external search did not answer. Do NOT treat this as 'no results exist' — it is not evidence of absence.")]
    public async Task NonSuccess_RelaysTheProxysMessageVerbatim(HttpStatusCode status, string reason, string message)
    {
        var body = JsonSerializer.Serialize(new { reason, message });
        var result = await Client(new StubHandler(status, body))
            .SearchAsync("yttrium forms", "discovery.candidate_forms", 10, default);

        Assert.Empty(result.Hits);
        Assert.Equal(message, result.Note);                   // VERBATIM — not paraphrased, not swallowed
    }

    /// A 500 from an App Gateway or Easy Auth sits in FRONT of the proxy and answers with HTML, not our
    /// SearchError. Deserialization then fails or yields null, and the client must still say something.
    [Theory]
    [InlineData("")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("null")]
    [InlineData("{\"unexpected\":true}")]
    public async Task NonSuccess_WithAnUnusableBody_StillYieldsANote_NeverSilentEmptiness(string body)
    {
        var result = await Client(new StubHandler(HttpStatusCode.BadGateway, body))
            .SearchAsync("yttrium forms", "discovery.candidate_forms", 10, default);

        Assert.Empty(result.Hits);
        Assert.False(string.IsNullOrWhiteSpace(result.Note));
    }

    /// A 200 with a body the client cannot read is the nastiest case, because IsSuccessStatusCode says all is
    /// well. It must not become hits=[] note=null.
    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task Ok_WithAnEmptyOrGarbageBody_DoesNotThrow_AndYieldsANote(string body)
    {
        var result = await Client(new StubHandler(HttpStatusCode.OK, body))
            .SearchAsync("yttrium forms", "discovery.candidate_forms", 10, default);

        Assert.Empty(result.Hits);
        Assert.False(string.IsNullOrWhiteSpace(result.Note));
    }

    /// THE ONE THAT MATTERS MOST. A dropped connection, a DNS failure, a private-endpoint misconfiguration —
    /// none of it is evidence about chemistry. The note says so in the words the agent needs to hear, and the
    /// assertion is deliberately written as "NOT silent emptiness" because that is the bug being prevented.
    [Fact]
    public async Task TransportFailure_YieldsANote_NotSilentEmptiness()
    {
        var result = await Client(new StubHandler(HttpStatusCode.OK, "", new HttpRequestException("no such host")))
            .SearchAsync("yttrium forms", "discovery.candidate_forms", 10, default);

        Assert.Empty(result.Hits);
        Assert.NotNull(result.Note);
        Assert.Contains("do NOT treat this as 'no results exist'", result.Note);
    }

    /// Cancellation is NOT a search failure — it is the host shutting down or the caller giving up, and it
    /// must propagate. Folding it into the generic catch would hand the agent "the external search is
    /// unavailable" for a run that was simply abandoned, and would let a cancelled Discovery run continue
    /// reasoning on a truncated tool result instead of stopping.
    [Fact]
    public async Task Cancellation_IsRethrown_NotReportedAsAFailedSearch()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new StubHandler(HttpStatusCode.OK, TwoHits))
                .SearchAsync("yttrium forms", "discovery.candidate_forms", 10, cts.Token));
    }

    [Fact]
    public async Task Request_IsPostedToTheProxysSearchRoute_WithABearerTokenForItsAudience()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoHits);
        var credential = new FakeCredential();
        await Client(handler, credential).SearchAsync("yttrium forms", "discovery.candidate_forms", 10, default);

        // The endpoint is configured with a trailing slash here on purpose: a double slash would 404 behind
        // Easy Auth and surface to the agent as a failed search.
        Assert.Equal("https://proxy.internal/api/search", handler.LastUri!.ToString());
        Assert.Equal("Bearer stub-token", handler.LastAuthorization);
        Assert.Equal(["api://search-proxy/.default"], credential.LastContext.Scopes);
    }
}
