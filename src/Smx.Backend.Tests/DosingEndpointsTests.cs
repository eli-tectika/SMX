using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class DosingEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly HttpClient _client;
    private const string P = "proj-test-1";

    public DosingEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
        })).CreateClient();

    private async Task SeedParkedProjectAsync()
    {
        var payload = JsonSerializer.SerializeToElement(new { }, Json.Options);
        var doc = ProjectDoc.Create(P, "Acme", "Bottle", payload);
        doc.Stages[Stages.Dosing].Status = "awaiting-operator";
        await _store.UpsertProjectAsync(doc);
    }

    private async Task SeedDosedProjectAsync()
    {
        await SeedParkedProjectAsync();
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "2026-07-15T00:00:00Z",
        });
    }

    [Fact]
    public async Task PostLoading_RecordsItCrossProject_AndReopensDosing()
    {
        // The write goes to the KNOWLEDGE layer (keyed by CAS, not by project), and re-opening the stage IS
        // the re-trigger — the ProjectDoc upsert is a change-feed event.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "1314-36-9", element = "Y", form = "oxide", metalLoading = 0.787, basis = "2×M(Y)/M(Y2O3)" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(0.787, (await _knowledge.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
        Assert.Equal("pending", (await _store.GetProjectAsync(P))!.Stages[Stages.Dosing].Status);
    }

    [Fact]
    public async Task PostLoading_IsReadByADIFFERENTProject_WhichNeverHasToAskAgain()
    {
        await SeedParkedProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "1314-36-9", element = "Y", form = "oxide", metalLoading = 0.787, basis = "b" });
        Assert.NotNull(await _knowledge.GetSubstancePropertyAsync("1314-36-9"));   // not scoped to P
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.2)]
    [InlineData(1.4)]
    public async Task PostLoading_RefusesAnImpossibleLoading_With422(double loading)
    {
        // 0 → an infinite order amount. >1 → more metal than compound, and it silently UNDER-orders.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "c", element = "Y", form = "oxide", metalLoading = loading, basis = "b" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Null(await _knowledge.GetSubstancePropertyAsync("c"));
    }

    [Fact]
    public async Task PostLoading_WithoutABasis_Is422()
    {
        // An unsourced number in the knowledge layer is worse than none: every future project inherits it
        // and nobody can check it.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "c", element = "Y", form = "oxide", metalLoading = 0.7, basis = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Null(await _knowledge.GetSubstancePropertyAsync("c"));   // 422 ⇒ nothing written, on the basis path too
    }

    [Fact]
    public async Task PostLoading_ToAnUnknownProject_Is404_AndWritesNothing()
    {
        // A 4xx must mean "nothing happened". The project existence check has to run BEFORE the knowledge
        // write — otherwise a valid loading+basis with a stale projectId commits a permanent cross-project
        // write (and re-stamps EnteredAt on retry) and only then 404s. Provenance must stay checkable.
        var res = await _client.PostAsJsonAsync("/projects/never-seeded/dosing/loading",
            new { cas = "1314-36-9", element = "Y", form = "oxide", metalLoading = 0.787, basis = "b" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(await _knowledge.GetSubstancePropertyAsync("1314-36-9"));   // the store stayed untouched
    }

    [Fact]
    public async Task PostReview_RecordsTheSoftCheckpoint_AndDoesNotBlockAnything()
    {
        // SOFT. It records that the code-finalization review happened. It is not a gate, it unlocks nothing.
        await SeedDosedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/review",
            new { note = "PL + physics reviewed the codes on 14 Jul; happy with the Y:Zr ratio" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var dosing = await _store.GetDosingAsync(P);
        Assert.Contains("Y:Zr", dosing!.ReviewNote);
        Assert.NotNull(dosing.ReviewedAt);
    }

    [Fact]
    public async Task PostReview_WithABlankNote_Is422()
    {
        await SeedDosedProjectAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await _client.PostAsJsonAsync($"/projects/{P}/dosing/review", new { note = "   " })).StatusCode);
    }

    [Fact]
    public async Task GetDosing_IsNotFound_BeforeTheStageHasRun() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/dosing")).StatusCode);
}
