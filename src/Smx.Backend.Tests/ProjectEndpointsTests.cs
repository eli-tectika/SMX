using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class ProjectEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ProjectEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private static readonly object ValidBody = new
    {
        client = "Acme", product = "Shampoo bottle",
        components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "brand" } },
        elementPools = new[] { new { component = "bottle", element = "Zr", line = "Kα", status = "V" } },
        clientRestrictedList = new[] { "Pb" },
    };

    [Fact]
    public async Task PostProjects_Returns202_AndSeedsProjectDoc()
    {
        var resp = await _client.PostAsJsonAsync("/projects", ValidBody);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("projectId").GetString()!;
        var doc = await _store.GetProjectAsync(id);
        Assert.NotNull(doc);
        Assert.Equal("pending", doc!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task PostProjects_Rejects_EmptyComponentsOrElementPools()
    {
        var resp = await _client.PostAsJsonAsync("/projects", new { client = "A", product = "P",
            components = Array.Empty<object>(), elementPools = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_WithElementPools_Returns202_AndSeedsProject()
    {
        var req = new CreateProjectRequest("Acme", "MUFE",
            Components: [new("bottle", "PET", "packaging", ["EU"], "brand")],
            ElementPools: [new("bottle", "Y", "Kα", "V", null)],
            Candidates: null,
            ClientRestrictedList: ["Pb"]);
        var resp = await _client.PostAsJsonAsync("/projects", req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Post_WithNeitherPoolsNorCandidates_Returns400()
    {
        var req = new CreateProjectRequest("Acme", "MUFE",
            Components: [new("bottle", "PET", "packaging", ["EU"], "brand")],
            ElementPools: [], Candidates: null, ClientRestrictedList: null);
        var resp = await _client.PostAsJsonAsync("/projects", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetProject_ReportsStageStatuses()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        var status = await _client.GetFromJsonAsync<JsonElement>($"/projects/{id}");
        Assert.Equal("pending", status.GetProperty("stages").GetProperty("intake").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetMatrix_404UntilAssembled_ThenReturnsJson()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{id}/matrix")).StatusCode);

        await _store.UpsertMatrixAsync(new MatrixDoc { Id = RecordIds.Matrix(id), ProjectId = id,
            Columns = ["bottle"], GeneratedAt = "t" });
        var resp = await _client.GetAsync($"/projects/{id}/matrix");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("bottle", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Healthz_Returns200()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
    }
}
