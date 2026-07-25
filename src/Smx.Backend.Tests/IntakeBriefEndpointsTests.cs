using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class IntakeBriefEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public IntakeBriefEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IRecordStore store) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)));

    [Fact]
    public async Task Get_ReturnsTheBrief_WithItsDossierAndTranscript()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertIntakeBriefAsync(new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief("proj-1"), ProjectId = "proj-1", SessionId = "isx-aaaa1111",
            Summary = "Acme make a 500 ml PET bottle.",
            CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Dossier = [new() { QuestionId = "raw-materials", State = DossierState.Answered,
                               Answer = "PET resin", Provenance = "operator" }],
            Transcript = [new() { Role = "operator", Text = "Acme, PET bottles.",
                                  CreatedAt = "2026-07-22T10:00:00.0000000Z" }],
        });
        using var app = NewApp(store);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/projects/proj-1/intake-brief");

        Assert.Equal("Acme make a 500 ml PET bottle.", body.GetProperty("summary").GetString());
        Assert.Equal("raw-materials", body.GetProperty("dossier")[0].GetProperty("questionId").GetString());
        // The transcript travels WITH the conclusions. When a regulatory verdict later hinges on the
        // operator having said the adhesive is water-based, that sentence must be in the record beside
        // the dossier row it produced — not summarised away.
        Assert.Equal("Acme, PET bottles.", body.GetProperty("transcript")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Get_IsA404_ForAProjectCreatedThroughTheForm()
    {
        // Every project created before this feature has no brief. That is a normal state, not an error
        // condition — the intake screen renders the record it does have and says so plainly.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().GetAsync("/projects/proj-nope/intake-brief")).StatusCode);
    }

    [Fact]
    public async Task Questions_ReturnTheWholeCatalogue_SoTheFrontendNeedsNoSecondCopy()
    {
        // The coverage line and the client-side gate mirror both need the full question set. A
        // hard-coded copy in TypeScript would be a second catalogue in a second language, and the two
        // would drift — which is the exact failure IntakeQuestions.Description exists to prevent on the
        // tool-description side.
        using var app = NewApp(new InMemoryRecordStore());

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/intake-questions");

        Assert.Equal(IntakeQuestions.All.Count, body.GetArrayLength());
        var ids = body.EnumerateArray().Select(q => q.GetProperty("id").GetString()).ToList();
        foreach (var q in IntakeQuestions.All) Assert.Contains(q.Id, ids);
        // `why` is shown to the OPERATOR when they expand "see what's open" — it is the difference
        // between a checklist and an explanation of what the analysis will be missing.
        Assert.False(string.IsNullOrWhiteSpace(body[0].GetProperty("why").GetString()));
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheBriefSurface()
    {
        // Trap 1's regression guard: a missing [FromServices] on any store parameter breaks routing for
        // the WHOLE app, and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
