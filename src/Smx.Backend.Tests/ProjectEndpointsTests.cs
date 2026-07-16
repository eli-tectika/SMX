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

    /// The same project, with the physicist's numbers attached: the batch MASS, the measured background level
    /// and the deployment device's LODs. These are the inputs the ppm detection floor is computed from.
    private static object PhysicsBody(string backgroundComponent = "bottle") => new
    {
        client = "Acme", product = "Shampoo bottle",
        components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "brand", batchMassKg = 250.0 } },
        elementPools = new[] { new { component = "bottle", element = "Zr", line = "Kα", status = "V" } },
        measuredBackground = new[] { new { component = backgroundComponent, element = "Zr", level = 4.0, unit = "ppm" } },
        device = new { model = "Olympus Vanta M", lods = new[] { new { element = "Zr", lod = 1.5, unit = "ppm" } } },
    };

    private async Task<JsonElement> PostAndReadPayloadAsync(object body)
    {
        var resp = await _client.PostAsJsonAsync("/projects", body);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        return (await _store.GetProjectAsync(id))!.Payload;
    }

    [Fact]
    public async Task PostProject_CarriesTheMeasuredBackgroundAndTheDevice_IntoThePayload()
    {
        // The payload is the only thing intake reads. Drop these on the floor here and the ppm detection floor
        // has no inputs at all — Dosing parks forever, and the one number that decides whether the marker is
        // physically readable in the field never gets computed.
        var payload = await PostAndReadPayloadAsync(PhysicsBody());

        Assert.Equal(4.0, payload.GetProperty("measuredBackground")[0].GetProperty("level").GetDouble());
        Assert.Equal("ppm", payload.GetProperty("measuredBackground")[0].GetProperty("unit").GetString());
        Assert.Equal("Olympus Vanta M", payload.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal(1.5, payload.GetProperty("device").GetProperty("lods")[0].GetProperty("lod").GetDouble());
        Assert.Equal(250.0, payload.GetProperty("components")[0].GetProperty("batchMassKg").GetDouble());
    }

    [Fact]
    public async Task PostProject_RefusesAMeasuredBackgroundForAnUndeclaredComponent()
    {
        // The same law the element pools already obey: a measurement that names no component is a measurement
        // of nothing. Accepting it would leave it sitting in the payload looking exactly like data, and the
        // component it was actually measured on would silently have no background at all.
        var resp = await _client.PostAsJsonAsync("/projects", PhysicsBody(backgroundComponent: "lid"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostProject_IsStillAcceptedWithNoPhysicsAtAll_BecauseItArrivesLater()
    {
        // Law 6: the physicist's XRF run happens OFFLINE and lands days later. Intake must not demand it up
        // front — Dosing PARKS on its absence, which is the whole point of the awaiting-physics state.
        // So absence has to be REPRESENTABLE in the payload: an empty list and no device key, which is what
        // IntakeAgent's payload deserializer reads back as "nothing measured yet".
        var payload = await PostAndReadPayloadAsync(ValidBody);

        Assert.Empty(payload.GetProperty("measuredBackground").EnumerateArray());
        Assert.False(payload.TryGetProperty("device", out _));
    }

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

    /// The intake payload used to be write-only across the whole API: submitted, held on the record, and
    /// readable by nothing. The intake screen's only recourse was to apologise for its own absence in prose.
    /// It is the operator's OWN input — never an agent's output — so returning it cannot launder a
    /// fabricated verdict; it is the safest data in the record to render.
    [Fact]
    public async Task GetProject_ReturnsTheIntakePayload_SoIntakeCanRenderWhatWasSubmitted()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        var body = await _client.GetFromJsonAsync<JsonElement>($"/projects/{id}");

        var payload = body.GetProperty("payload");
        Assert.Equal("bottle", payload.GetProperty("components")[0].GetProperty("id").GetString());
        Assert.Equal("HDPE", payload.GetProperty("components")[0].GetProperty("material").GetString());
        Assert.Equal("Zr", payload.GetProperty("elementPools")[0].GetProperty("element").GetString());
        Assert.Equal("Pb", payload.GetProperty("clientRestrictedList")[0].GetString());
    }

    /// Absence has to survive the round trip, because it is not cosmetic: an empty measuredBackground and
    /// no device key IS the awaiting-physics precondition Dosing parks on. A projection that dropped these
    /// keys, or emitted a null device, would leave the intake screen unable to tell "no XRF yet" from
    /// "never asked" — and those are different facts.
    [Fact]
    public async Task GetProject_ReportsAbsentPhysics_AsAnEmptyListAndNoDeviceKey()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        var payload = (await _client.GetFromJsonAsync<JsonElement>($"/projects/{id}")).GetProperty("payload");

        Assert.Empty(payload.GetProperty("measuredBackground").EnumerateArray());
        Assert.False(payload.TryGetProperty("device", out _));
    }

    [Fact]
    public async Task GetProject_ReturnsThePhysicsInputs_WhenTheyAreOnFile()
    {
        var post = await _client.PostAsJsonAsync("/projects", PhysicsBody());
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        var payload = (await _client.GetFromJsonAsync<JsonElement>($"/projects/{id}")).GetProperty("payload");

        var background = payload.GetProperty("measuredBackground")[0];
        Assert.Equal(4.0, background.GetProperty("level").GetDouble());
        // The unit travels WITH the level, always. A level read without its unit is the exact confusion
        // DetectionFloor refuses to make — ppm and counts are not interchangeable.
        Assert.Equal("ppm", background.GetProperty("unit").GetString());
        Assert.Equal("Olympus Vanta M", payload.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal(250.0, payload.GetProperty("components")[0].GetProperty("batchMassKg").GetDouble());
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
