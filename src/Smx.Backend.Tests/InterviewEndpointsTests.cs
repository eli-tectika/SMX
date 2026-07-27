using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Api;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The interview's streaming turn, per event. It is served by the backend itself now — there is no second
/// host and no SSE relay — but the test still builds a minimal WebApplication + TestServer rather than
/// Smx.Backend's own Program: the surface under test is Smx.Backend.Api.InterviewEndpoints alone, and an
/// inline app keeps the DI graph to exactly what it needs (IIntakeSessionStore, IRecordStore,
/// IAttachmentBlobStore, IAgentRuns) — the same fakes FakeAgentRunsSmokeTests and DosingCostEndToEndTests
/// already use. That the route ALSO works through the real Program, streaming as it goes, is
/// IntakeSessionEndpointsTests.Messages_ReachTheClientAsTheAgentProducesThem_NotInOneLumpAtTheEnd.
///
/// This project targets net10.0 for a reason that bears on any test that hosts endpoints: the net8-targeted
/// Microsoft.AspNetCore.TestHost's `ResponseBodyPipeWriter` does not implement `PipeWriter.UnflushedBytes`,
/// which the System.Text.Json shipped with the only-installed net10 runtime requires of ANY PipeWriter it
/// serializes onto — so `Results.Ok(...)` and `WriteAsJsonAsync` both throw the instant a minimal API tries
/// to write a JSON response under that combination.
public class InterviewEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryIntakeSessionStore _sessions = new();
    private readonly InMemoryRecordStore _records = new();
    private readonly InMemoryAttachmentBlobStore _blobs = new();
    private readonly FakeAgentRuns _runs = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IIntakeSessionStore>(_sessions);
                    services.AddSingleton<IRecordStore>(_records);
                    services.AddSingleton<IAttachmentBlobStore>(_blobs);
                    services.AddSingleton<IAgentRuns>(_runs);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Mirrors Program.cs: /healthz beside the interview surface, in the same app — this
                        // is the regression test for Trap #1 (a missing [FromServices] on any store
                        // parameter below breaks routing for the WHOLE app, /healthz included).
                        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
                        endpoints.MapInterviewEndpoints();
                    });
                });
            })
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task Seed(string sessionId) => _sessions.UpsertAsync(new IntakeSessionDoc
    {
        Id = sessionId, SessionId = sessionId, CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
    });

    private Task<HttpResponseMessage> PostMessage(string sessionId, string text) =>
        _client.PostAsJsonAsync($"/intake-sessions/{sessionId}/messages", new { text });

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheInterviewSurface()
    {
        var resp = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PostMessage_Streams_AndPersistsBothTurns()
    {
        await Seed("s1");
        var resp = await PostMessage("s1", "hello");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("event: chunk", body);
        Assert.Contains("event: done", body);
        Assert.Contains("Echo: hello", body); // FakeAgentRuns.Interview's default echo

        var saved = await _sessions.GetAsync("s1");
        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Turns.Count);
        Assert.Equal("operator", saved.Turns[0].Role);
        Assert.Equal("hello", saved.Turns[0].Text);
        Assert.Equal("agent", saved.Turns[1].Role);
        Assert.Equal("Echo: hello", saved.Turns[1].Text);
    }

    [Fact]
    public async Task PostMessage_Blank_Is422_AndPersistsNothing()
    {
        await Seed("s1");
        var resp = await PostMessage("s1", "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var saved = await _sessions.GetAsync("s1");
        Assert.Empty(saved!.Turns);
    }

    [Fact]
    public async Task PostMessage_ToAnUnknownSession_Is404()
    {
        var resp = await PostMessage("ghost", "hello");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
