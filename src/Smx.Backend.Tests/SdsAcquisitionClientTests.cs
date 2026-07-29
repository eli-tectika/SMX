using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Tools;
using Smx.Infrastructure.Sds;

namespace Smx.Backend.Tests;

/// Like SearchProxyClientTests, this exists because of what the client tells the CALLER when the far side
/// is down. `regsync` being unreachable must degrade an agent's turn, never end it — an exception here
/// would propagate out of a tool call and kill the stage, which is precisely the "park and wait days"
/// behaviour the 2026-07-29 redesign set out to remove.
public class SdsAcquisitionClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, Exception? throws = null) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";
        public string? LastAuthorization { get; private set; }
        public Uri? LastUri { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
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
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct)
            => new("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct)
            => new(GetToken(requestContext, ct));
    }

    private static SdsAcquisitionClient Client(StubHandler handler) => new(
        new HttpClient(handler), new FakeCredential(), "https://regsync.example", "api://regsync",
        NullLogger<SdsAcquisitionClient>.Instance);

    [Fact]
    public async Task Sends_the_cas_and_parses_the_result()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"status":"fetched","registryId":"r1","supplier":"Acme","revisionDate":"2026-07-29"}""");

        var result = await Client(handler).EnsureAsync("1310-73-2", "Na", "hydroxide", default);

        Assert.Equal(SdsEnsureStatus.Fetched, result.Status);
        Assert.Equal("r1", result.RegistryId);
        Assert.Equal("Acme", result.Supplier);
        Assert.True(result.Have);
        Assert.Contains("1310-73-2", handler.LastRequestBody);
        Assert.Contains("/api/sds/ensure", handler.LastUri!.ToString());
        Assert.Equal("Bearer stub-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task An_unavailable_sheet_carries_the_attempts_through()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"status":"unavailable","reason":"no candidate validated","attempted":[
              {"url":"https://a.example/x.pdf","supplier":"a.example","outcome":"rejected: CAS not in document"}]}
            """);

        var result = await Client(handler).EnsureAsync("18865-74-2", "Zr", "TMHD complex", default);

        Assert.Equal(SdsEnsureStatus.Unavailable, result.Status);
        Assert.False(result.Have);
        var attempt = Assert.Single(result.Attempts);
        Assert.Contains("CAS not in document", attempt.Outcome);
    }

    // regsync down must be an answer, not an exception.
    [Fact]
    public async Task A_transport_failure_becomes_unavailable_rather_than_throwing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "", new HttpRequestException("connection refused"));

        var result = await Client(handler).EnsureAsync("1310-73-2", "Na", "hydroxide", default);

        Assert.Equal(SdsEnsureStatus.Unavailable, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task A_server_error_becomes_unavailable_rather_than_throwing()
    {
        var result = await Client(new StubHandler(HttpStatusCode.InternalServerError, "boom"))
            .EnsureAsync("1310-73-2", "Na", "hydroxide", default);

        Assert.Equal(SdsEnsureStatus.Unavailable, result.Status);
        Assert.Contains("500", result.Reason);
    }

    // Cancellation is the one thing that must still propagate — it means the caller went away, and
    // swallowing it would turn a cancelled turn into a fabricated "we tried and could not get it".
    [Fact]
    public async Task Cancellation_still_propagates()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "", new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).EnsureAsync("1310-73-2", "Na", "hydroxide", default));
    }

    // A ledger append is bookkeeping and must never fail the pipeline stage that triggered it.
    [Fact]
    public async Task Append_swallows_failure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "", new HttpRequestException("down"));
        await Client(handler).AppendAsync("Zr", "TMHD complex", "18865-74-2", default);   // must not throw
    }

    [Fact]
    public async Task Append_posts_the_substance()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"added":true}""");

        await Client(handler).AppendAsync("Zr", "TMHD complex", "18865-74-2", default);

        Assert.Contains("18865-74-2", handler.LastRequestBody);
        Assert.Contains("/api/sds/master-list", handler.LastUri!.ToString());
    }
}
