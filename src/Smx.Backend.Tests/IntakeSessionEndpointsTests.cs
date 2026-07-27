using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The pre-project interview's HTTP surface as the browser meets it: session create/read here, the
/// streaming turn's own behaviour in InterviewEndpointsTests. Both routes are served by this one app —
/// there is no second host and no proxy hop any more.
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
    public async Task Messages_ReachTheClientAsTheAgentProducesThem_NotInOneLumpAtTheEnd()
    {
        // This used to test the SSE PROXY (backend → orchestrator). There is no proxy any more — the
        // agent runs in this process — but the guarantee it existed to protect is unchanged and is
        // exactly as easy to lose: an interview turn is only worth streaming if each event leaves the
        // server when it is produced. Nothing else in the suite would notice this path starting to
        // buffer, because a test that simply reads the finished body cannot tell the difference.
        //
        // The gate makes it decisive rather than a timing guess: the fake agent refuses to yield its
        // LAST chunk until the client has already read the first event off the wire. If anything on the
        // path buffers, this deadlocks and the test times out.
        var firstEventSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new InMemoryIntakeSessionStore();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = "isx-abc", SessionId = "isx-abc", CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        var runs = new FakeAgentRuns { Interview = (_, _, _) => GatedChunks(firstEventSeen) };
        using var app = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IIntakeSessionStore>(sessions);
            s.AddSingleton<IRecordStore>(new InMemoryRecordStore());
            s.AddSingleton<IAttachmentBlobStore>(new InMemoryAttachmentBlobStore());
            s.AddSingleton<IAgentRuns>(runs);
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
            // Releases the fake agent's remaining chunk. Reaching this at all proves the first event
            // crossed the whole stack while the turn was still running.
            if (seen.Count > 0) firstEventSeen.TrySetResult();
        }

        Assert.Equal(["chunk", "chunk", "done"], seen);
    }

    /// Two chunks with a gate between them: the second is withheld until the client has read the first.
    private static async IAsyncEnumerable<string> GatedChunks(TaskCompletionSource gate)
    {
        yield return "Hel";
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        yield return "lo";
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
