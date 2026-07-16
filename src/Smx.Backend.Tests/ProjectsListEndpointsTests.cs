using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// GET /projects — the estate list. Newest-first, each row carrying the stage spine and both gate
/// statuses, because the landing page's "Needs signing" card cannot be computed from anything less.
public class ProjectsListEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ProjectsListEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private static ProjectDoc Project(string id, string client, string product, string createdAt)
    {
        var doc = ProjectDoc.Create(id, client, product, JsonSerializer.SerializeToElement(new { }));
        doc.CreatedAt = createdAt;
        return doc;
    }

    [Fact]
    public async Task GetProjects_ListsNewestFirst_WithStagesAndGates()
    {
        // Seeded OLDER first: [newer, older] on the wire can only come from the ORDER BY, not from
        // insertion order echoing back.
        await _store.UpsertProjectAsync(Project("proj-older", "Acme", "Shampoo bottle", "2026-07-15T10:00:00.0000000+00:00"));
        await _store.UpsertProjectAsync(Project("proj-newer", "Globex", "Serum label", "2026-07-16T09:00:00.0000000+00:00"));
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("proj-older", GateTypes.Regulatory), ProjectId = "proj-older",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-07-15T12:00:00.0000000+00:00",
        });

        var resp = await _client.GetAsync("/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("proj-newer", arr[0].GetProperty("projectId").GetString());
        Assert.Equal("proj-older", arr[1].GetProperty("projectId").GetString());
        Assert.Equal("Globex", arr[0].GetProperty("client").GetString());
        Assert.Equal("Serum label", arr[0].GetProperty("product").GetString());
        Assert.Equal("2026-07-16T09:00:00.0000000+00:00", arr[0].GetProperty("createdAt").GetString());

        // The stage spine rides along, statuses included — straight off the ProjectDoc.
        Assert.Equal("pending", arr[0].GetProperty("stages").GetProperty("intake").GetProperty("status").GetString());
        // `cost`, deliberately: the last stage that exists on MAIN today. The plan-5 branch asserts
        // `decision` here (its Tasks 1+2 seed that stage); when the branch merges, take ITS version.
        Assert.Equal("pending", arr[1].GetProperty("stages").GetProperty("cost").GetProperty("status").GetString());

        // The gated project reports its signed gate; everywhere a gate is absent the key is an EXPLICIT
        // null — "no gate yet" must be a value the frontend can read, not a missing field it has to infer.
        Assert.Equal("approved", arr[1].GetProperty("gates").GetProperty("regulatory").GetString());
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("gates").GetProperty("regulatory").ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("gates").GetProperty("vp").ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[1].GetProperty("gates").GetProperty("vp").ValueKind);
    }

    [Fact]
    public async Task GetProjects_EmptyStore_ReturnsEmptyArray()
    {
        // Cold start is an empty estate, not an error: [] — never 404.
        var resp = await _client.GetAsync("/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(0, arr.GetArrayLength());
    }
}
