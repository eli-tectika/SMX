using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

/// The `learned-conclusions` index does not exist until Plan 3b writes to it, so EVERY Intake and
/// Discovery run in the meantime queries a missing index. The whole cold-start story rests on one
/// `catch (RequestFailedException e) when (e.Status == 404)` — if the exception type or the status
/// were wrong, every run would throw and nothing would notice. These tests drive the real Azure SDK
/// pipeline through a stub transport so the catch is exercised, not simulated.
public class LearnedConclusionsSearchToolTests
{
    /// Returns a canned response for whatever the Search SDK sends. Wrapped in HttpClientTransport,
    /// this is a real HttpPipelineTransport: the SDK does its own status handling and exception
    /// construction, so the RequestFailedException under test is the genuine article.
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }

    private static LearnedConclusionsSearchTool Tool(HttpStatusCode status, string body = "{}")
    {
        var options = new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(new StubHandler(status, body))) };
        // Retries would make a 5xx test slow; nothing here is retried on 404 anyway.
        options.Retry.MaxRetries = 0;
        var client = new SearchClient(
            new Uri("https://stub.search.windows.net"), "learned-conclusions", new AzureKeyCredential("stub"), options);
        return new LearnedConclusionsSearchTool(client);
    }

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
}
