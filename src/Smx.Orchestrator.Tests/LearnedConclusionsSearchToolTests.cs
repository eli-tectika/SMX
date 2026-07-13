using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Smx.Domain.Tools;
using Smx.Infrastructure.Search;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The `learned-conclusions` index does not exist until the first conclusion is pushed, so EVERY Intake
/// and Discovery run in the meantime queries a missing index. The whole cold-start story rests on one
/// `catch (RequestFailedException e) when (e.Status == 404)` — if the exception type or the status
/// were wrong, every run would throw and nothing would notice. These tests drive the real Azure SDK
/// pipeline through a stub transport so the catch is exercised, not simulated.
///
/// The other half of what is tested here is that the query is HYBRID. A keyword-only tool over this index
/// is a silent no-op (see Search_SendsAHybridQuery_KeywordPlusVector), and a silent no-op is precisely the
/// failure this whole subsystem cannot have.
public class LearnedConclusionsSearchToolTests
{
    /// Returns a canned response for whatever the Search SDK sends, and records the request body it was
    /// sent. Wrapped in HttpClientTransport, this is a real HttpPipelineTransport: the SDK does its own
    /// serialization, status handling and exception construction, so both the JSON asserted on below and
    /// the RequestFailedException under test are the genuine article.
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    private sealed class ThrowingEmbedder(int status) : IEmbedder
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new RequestFailedException(status, $"embeddings: {status}");
    }

    private static SearchClient Client(StubHandler handler)
    {
        var options = new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        // Retries would make a 5xx test slow; nothing here is retried on 404 anyway.
        options.Retry.MaxRetries = 0;
        return new SearchClient(
            new Uri("https://stub.search.windows.net"), "learned-conclusions", new AzureKeyCredential("stub"), options);
    }

    private static LearnedConclusionsSearchTool Tool(HttpStatusCode status, string body = "{}") =>
        new(Client(new StubHandler(status, body)), new FakeEmbedder());

    [Fact]
    public async Task Search_MissingIndex_DegradesToNoMatches_DoesNotThrow()
    {
        const string notFound = """
            {"error":{"code":"","message":"The index 'learned-conclusions' for service 'stub' was not found."}}
            """;
        var results = await Tool(HttpStatusCode.NotFound, notFound).SearchAsync("zr bottle");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_OtherFailures_StillThrow()
    {
        // The catch must be surgical: only a missing index degrades. A 403 (misconfigured identity) or a
        // 500 is a real fault — swallowing it would let an agent silently reason with zero prior evidence.
        var forbidden = await Assert.ThrowsAsync<RequestFailedException>(
            () => Tool(HttpStatusCode.Forbidden).SearchAsync("zr bottle"));
        Assert.Equal(403, forbidden.Status);

        var error = await Assert.ThrowsAsync<RequestFailedException>(
            () => Tool(HttpStatusCode.InternalServerError).SearchAsync("zr bottle"));
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task Search_SendsAHybridQuery_KeywordPlusVector()
    {
        // A conclusion is written in the operator's words ("overlaps the Ti K-beta line"); an agent asks in
        // its own ("is Ba safe for an HDPE bottle?"). Those share almost no terms, so BM25 alone finds
        // nothing and the tool reports "no prior conclusions" — silently switching the whole knowledge loop
        // off. The vector query is what bridges them, so assert it reaches the wire, not just the options.
        var handler = new StubHandler(HttpStatusCode.OK, """{"value":[]}""");
        var embedder = new FakeEmbedder();
        var tool = new LearnedConclusionsSearchTool(Client(handler), embedder);

        await tool.SearchAsync("is Ba safe to tier for an HDPE bottle?");

        Assert.Equal(["is Ba safe to tier for an HDPE bottle?"], embedder.Embedded); // the QUERY is embedded
        Assert.Contains("vectorQueries", handler.LastRequestBody);
        Assert.Contains("contentVector", handler.LastRequestBody);                   // against the right field
        Assert.Contains("is Ba safe to tier", handler.LastRequestBody);              // BM25 term still sent
    }

    [Theory]
    [InlineData(404)] // Azure OpenAI's DeploymentNotFound — a WRONG EMBEDDING_DEPLOYMENT name returns 404.
    [InlineData(403)] // embeddings identity misconfigured.
    [InlineData(500)]
    public async Task Search_EmbedderFailure_Propagates_NotReportedAsNoPriorConclusions(int status)
    {
        // The embed call sits OUTSIDE the 404 catch on purpose, and 404 is the case that proves it: the
        // embeddings endpoint answers a misconfigured deployment name with a 404 of its own. Move the embed
        // inside the try and THAT 404 lands in the cold-start catch — so a one-character typo in
        // EMBEDDING_DEPLOYMENT would make every search return [], which ToolBox renders to the agent as
        // "no prior conclusions on this". The knowledge layer would report itself permanently empty while
        // being perfectly healthy, and every test here would stay green. Hence a 404 embedder, not just a 403.
        var tool = new LearnedConclusionsSearchTool(
            Client(new StubHandler(HttpStatusCode.OK, """{"value":[]}""")), new ThrowingEmbedder(status));

        var e = await Assert.ThrowsAsync<RequestFailedException>(() => tool.SearchAsync("zr bottle"));
        Assert.Equal(status, e.Status);
    }

    [Fact]
    public async Task Search_MapsAHit_ToRetrievedChunk_WithIndexQualifiedReference()
    {
        // The happy path: `id` and `content` are the only two fields the tool reads off a hit (the schema
        // comment in LearnedConclusionsIndex leans on exactly that), and Reference must be index-qualified
        // so a citation points back at the document it came from.
        const string hit = """
            {"value":[{"@search.score":0.0163,"id":"lc-42","content":"barium overlaps the titanium K-beta line at our XRF settings"}]}
            """;
        var tool = new LearnedConclusionsSearchTool(
            Client(new StubHandler(HttpStatusCode.OK, hit)), new FakeEmbedder());

        var results = await tool.SearchAsync("is Ba safe to tier for an HDPE bottle?");

        var chunk = Assert.Single(results);
        Assert.Equal("learned-conclusions", chunk.Source);
        Assert.Equal("learned-conclusions/lc-42", chunk.Reference);
        Assert.Equal("barium overlaps the titanium K-beta line at our XRF settings", chunk.Content);
        Assert.Equal(0.0163, chunk.Score, 4);
    }

    [Fact]
    public async Task Search_EmptyQuery_ThrowsRatherThanReportingNoPriorConclusions()
    {
        // A degenerate arg from the model. It must not reach the wire: an empty `search` is Azure's
        // match-ALL, so a keyword+vector query on "" would hand the agent arbitrary prior conclusions it
        // never asked for. Nor may it return [] — that is the "no prior conclusions" sentinel, and emitting
        // it for a question nobody asked is the same silent lie this tool exists to prevent. So: throw. The
        // model sees a tool error and re-asks with a real query.
        var tool = new LearnedConclusionsSearchTool(
            Client(new StubHandler(HttpStatusCode.OK, """{"value":[]}""")), new FakeEmbedder());

        await Assert.ThrowsAsync<ArgumentException>(() => tool.SearchAsync("   "));
    }
}
