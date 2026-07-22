using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// POST /projects/{id}/start — the operator's signature that the dossier the interview agent wrote is
/// right. There is no agent tool for this and there never will be (design §2.3): creating a project is
/// safe to delegate because it runs nothing, but starting the analysis is the human asserting correctness.
public class ProjectStartEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProjectStartEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // A distinct IRecordStore per test (unlike ChatEndpointsTests' one shared field): several tests here
    // need to seed a specific project BEFORE the app is built, so the store is a parameter, not a
    // fixture-level constant. Matches IntakeSessionEndpointsTests' NewApp shape.
    private WebApplicationFactory<Program> NewApp(IRecordStore store) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)));

    private static List<ComponentSpec> OneGoodComponent() =>
        [new ComponentSpec("bottle", "PET", "food contact", ["EU"], "brand")];

    /// The same payload shape POST /projects writes and create_project writes — so a start test
    /// exercises the document the pipeline will actually read, not a convenient stand-in.
    private static JsonElement Payload(List<ComponentSpec> components) =>
        JsonSerializer.SerializeToElement(new
        {
            components,
            elementPools = Array.Empty<object>(),
            providedCandidates = Array.Empty<object>(),
            clientRestrictedList = Array.Empty<string>(),
            measuredBackground = Array.Empty<object>(),
        }, Json.Options);

    [Fact]
    public async Task Start_FlipsAwaitingConfirmationToPending()
    {
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create("proj-1", "Acme", "MUFE", Payload(OneGoodComponent()),
            intakeStatus: StageStatus.AwaitingConfirmation);
        await store.UpsertProjectAsync(project);
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/start", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(StageStatus.Pending, (await store.GetProjectAsync("proj-1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task Start_SucceedsWithNoElementPools()
    {
        // Design §2.4: a project reaches start with a confirmed component set and NO measured
        // background. That is the normal case — the physicist's XRF run lands days later and the stage
        // that needs it PARKS, exactly as Dosing already parks. Requiring pools here would make every
        // interview-created project unstartable.
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create("proj-1", "Acme", "MUFE",
            Payload(OneGoodComponent()), intakeStatus: StageStatus.AwaitingConfirmation));
        using var app = NewApp(store);

        Assert.Equal(HttpStatusCode.Accepted,
            (await app.CreateClient().PostAsync("/projects/proj-1/start", null)).StatusCode);
    }

    [Fact]
    public async Task Start_RefusesAProjectWithNoComponents()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create("proj-1", "Acme", "MUFE",
            Payload([]), intakeStatus: StageStatus.AwaitingConfirmation));
        using var app = NewApp(store);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.CreateClient().PostAsync("/projects/proj-1/start", null)).StatusCode);
    }

    [Fact]
    public async Task Start_RefusesAComponentWithNoMarkets()
    {
        // Zero markets EMPTIES that component's regulatory screen — a false-pass mechanism, not an
        // innocuous omission. The 422 message must say so, not just "invalid".
        var store = new InMemoryRecordStore();
        var noMarkets = new List<ComponentSpec> { new("bottle", "PET", "food contact", [], "brand") };
        await store.UpsertProjectAsync(ProjectDoc.Create("proj-1", "Acme", "MUFE",
            Payload(noMarkets), intakeStatus: StageStatus.AwaitingConfirmation));
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/start", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("regulatory screen", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Start_IsIdempotent_AndDoesNotRestartARunningProject()
    {
        // At-least-once everywhere else in this system; a double-press must not re-dispatch a stage
        // that is already running or done.
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create("proj-1", "Acme", "MUFE", Payload(OneGoodComponent()));
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        await store.UpsertProjectAsync(project);
        using var app = NewApp(store);

        await app.CreateClient().PostAsync("/projects/proj-1/start", null);

        Assert.Equal(StageStatus.Done, (await store.GetProjectAsync("proj-1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task Start_IsA404ForAnUnknownProject()
    {
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().PostAsync("/projects/nope/start", null)).StatusCode);
    }
}
