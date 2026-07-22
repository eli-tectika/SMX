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
    public async Task Healthz_StillRoutes_BesideTheIntakeSessionSurface()
    {
        // The regression test for trap 1: a missing [FromServices] on any store parameter above breaks
        // routing for the WHOLE app, /healthz included — and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryIntakeSessionStore());
        var resp = await app.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
