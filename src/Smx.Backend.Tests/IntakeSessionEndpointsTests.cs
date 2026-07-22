using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The backend's side of the pre-project interview (Task 11). The backend owns session CRUD and JWT
/// validation; the orchestrator owns the agent — Task 10's InterviewEndpointsTests covers that side.
/// This class covers session create/read and that the SSE proxy route builds without breaking routing
/// for the rest of the app (trap 1).
public class IntakeSessionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IntakeSessionEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // A distinct IIntakeSessionStore per test (unlike ChatEndpointsTests' one shared field): several
    // tests here need to seed a specific store BEFORE the app is built, so the store is a parameter,
    // not a fixture-level constant.
    private WebApplicationFactory<Program> NewApp(IIntakeSessionStore sessions) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(sessions)));

    [Fact]
    public async Task Post_CreatesASession_AndReturnsAnIdSafeId()
    {
        using var app = NewApp(new InMemoryIntakeSessionStore());
        var client = app.CreateClient();

        var res = await client.PostAsJsonAsync("/intake-sessions", new { client = "Acme", product = "MUFE" });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[A-Za-z0-9_-]+$", body.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Get_ReturnsTheTranscriptAndDossier_SoAReloadResumesTheInterview()
    {
        // Law 6: the operator closes the tab and comes back. The record is the conversation.
        var sessions = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-21T10:00:00.0000000Z",
            Turns = [new() { Role = "operator", Text = "Acme", CreatedAt = "2026-07-21T10:00:00.0000000Z" }],
        });
        using var app = NewApp(sessions);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/intake-sessions/{id}");

        Assert.Equal("Acme", body.GetProperty("turns")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Get_IsA404ForAnUnknownSession()
    {
        // Unlike a chat thread (where "nothing said here" is honest), an unknown session id is a real
        // error: the browser is holding an id for something that expired or never existed, and telling
        // it "empty interview" would silently start a second conversation nobody can find.
        using var app = NewApp(new InMemoryIntakeSessionStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().GetAsync("/intake-sessions/isx-nope")).StatusCode);
    }

    [Fact]
    public async Task Messages_ProxiesTheOrchestratorsEventsToTheClient_WithoutWaitingForTheTurnToEnd()
    {
        // The proxy's ONLY job is to relay without re-buffering, and nothing else in the suite would
        // notice it starting to. Losing that turns the whole streaming path — proven end to end in
        // MafStreamingPathTests, flushed per event by the orchestrator — into a one-lump arrival at the
        // last hop, which no test that simply reads the finished body could see.
        //
        // The gate makes this decisive rather than a timing guess: the fake orchestrator refuses to send
        // its LAST event until the client has already read the first one. If anything on the path
        // buffers, this deadlocks and the test times out.
        var firstEventSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new InMemoryIntakeSessionStore();
        using var app = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IIntakeSessionStore>(sessions);
            s.AddHttpClient(Api.IntakeSessionEndpoints.OrchestratorClient)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeOrchestrator(firstEventSeen))
                .ConfigureHttpClient(c => c.BaseAddress = new Uri("http://orchestrator.internal"));
        }));

        // ResponseHeadersRead on the CLIENT too: HttpClient's default buffers the whole body before
        // returning, which would hide the very thing this test is checking.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/intake-sessions/isx-abc/messages")
        {
            Content = JsonContent.Create(new { text = "hello" }),
        };
        using var res = await app.CreateClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        await using var body = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);

        var seen = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) seen.Add(line["event: ".Length..]);
            // Releases the fake orchestrator's final event. Reaching this at all proves the first event
            // crossed the proxy while the upstream response was still open.
            if (seen.Count > 0) firstEventSeen.TrySetResult();
        }

        Assert.Equal(["chunk", "done"], seen);
    }

    /// A stand-in for the orchestrator's SSE surface. Writes into a real pipe so the response body is
    /// genuinely streamed — a plain custom HttpContent would buffer itself into a MemoryStream and fake
    /// up a passing result no matter what the proxy did.
    private sealed class FakeOrchestrator(TaskCompletionSource gate) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pipe = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                var stream = pipe.Writer.AsStream();
                async Task Write(string s)
                {
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(s), ct);
                    await stream.FlushAsync(ct);
                }
                try
                {
                    await Write("event: chunk\ndata: {\"text\":\"Hel\"}\n\n");
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                    await Write("event: done\ndata: {\"createdProjectId\":null}\n\n");
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception e) { await pipe.Writer.CompleteAsync(e); }
            }, ct);

            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream()),
            };
            res.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheIntakeSessionSurface()
    {
        // The regression test for trap 1: a missing [FromServices] on any store parameter above breaks
        // routing for the WHOLE app, /healthz included — and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryIntakeSessionStore());
        var resp = await app.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
