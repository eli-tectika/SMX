using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Xrf;

namespace Smx.Backend.Tests;

public class XrfEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public XrfEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IRecordStore store) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)));

    private static async Task<InMemoryRecordStore> StoreWithProject()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
            Components = [new("bottle", "PET", "food contact", ["EU"], "brand")],
        });
        return store;
    }

    private static MultipartFormDataContent File(string name, string content)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        return form;
    }

    private const string GoodCsv =
        "component,element,line,status,signal_note,background_level,background_unit," +
        "device_model,device_lod,device_lod_unit\n" +
        "bottle,Ba,Ka,V,,12.5,ppm,Niton XL5,3.0,ppm\n";

    private static object OneRow(double level = 12.5, string element = "Ba", string component = "bottle",
                                 string status = "V", string? note = null) => new
    {
        rowNumber = 2, component, element, line = "Ka", status,
        signalNote = note, backgroundLevel = level, backgroundUnit = "ppm",
        deviceModel = "Niton XL5", deviceLod = 3.0, deviceLodUnit = "ppm",
        problems = Array.Empty<string>(),
    };

    [Fact]
    public async Task Parse_ReturnsProposals_AndWritesNothing()
    {
        // The parse endpoint is PURE. Two entry points that both write physics data would mean two
        // places a partial background can live, and "which of these is the real one" is a question
        // this system must never have to answer.
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/xrf/parse", File("r.csv", GoodCsv));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ba", body.GetProperty("proposals")[0].GetProperty("element").GetString());
        Assert.Empty((await store.GetConstraintsAsync("proj-1"))!.ElementPools);
    }

    [Fact]
    public async Task Parse_ReportsAnUnreadableFileAsAProblem_NotAsA500()
    {
        // A file we cannot open is the operator's problem to fix, not a server error. A 500 reads as
        // "the system is broken" when the answer is "save it as .csv".
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/xrf/parse", File("scan.pdf", "%PDF"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Confirm_WritesPoolsBackgroundsAndDeviceOntoTheConstraints()
    {
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsJsonAsync(
            "/projects/proj-1/xrf/confirm", new { proposals = new[] { OneRow() } });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var c = (await store.GetConstraintsAsync("proj-1"))!;
        Assert.Equal("Ba", Assert.Single(c.ElementPools).Element);
        Assert.Equal(12.5, Assert.Single(c.MeasuredBackgrounds).Level);
        Assert.Equal("Niton XL5", c.Device!.Model);
    }

    [Fact]
    public async Task Confirm_RefusesAConditionalRowWithNoNote_AndWritesNothing()
    {
        // The anti-rubber-stamping rule at the door that replaced the creation form. The "writes
        // nothing" half is the point: a partial write would leave the record holding some of a
        // refused confirmation.
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsJsonAsync("/projects/proj-1/xrf/confirm",
            new { proposals = new[] { OneRow(element: "Sr", status: "L", note: null) } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty((await store.GetConstraintsAsync("proj-1"))!.ElementPools);
    }

    [Fact]
    public async Task Confirm_ReplacesAnEarlierConfirmation_RatherThanAppendingToIt()
    {
        // A re-measure is a CORRECTION, not an addition. Appending would leave two measurements of the
        // same element in the record — which DetectionFloor then refuses to compute a floor from, so
        // the operator's fix would break dosing instead of repairing it.
        var store = await StoreWithProject();
        using var app = NewApp(store);
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/projects/proj-1/xrf/confirm", new { proposals = new[] { OneRow(12.5) } });
        await client.PostAsJsonAsync("/projects/proj-1/xrf/confirm", new { proposals = new[] { OneRow(9.0) } });

        var c = (await store.GetConstraintsAsync("proj-1"))!;
        Assert.Equal(9.0, Assert.Single(c.MeasuredBackgrounds).Level);
    }

    [Fact]
    public async Task Confirm_AcceptsARowThatOmitsTheProblemsArrayEntirely()
    {
        // `problems` is IReadOnlyList<string> on a positional record, so STJ binds an OMITTED field to
        // null rather than to an empty list — and XrfConfirmation.Build reads p.Problems.Count, which
        // NREs into a 500. Every other test here sends `problems: []` explicitly, so nothing else
        // covers the shape a hand-written client actually produces. This pins the normalisation at the
        // door; delete it and this test 500s.
        var store = await StoreWithProject();
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsJsonAsync("/projects/proj-1/xrf/confirm", new
        {
            proposals = new[] { new
            {
                rowNumber = 2, component = "bottle", element = "Ba", line = "Ka", status = "V",
                backgroundLevel = 12.5, backgroundUnit = "ppm",
                deviceModel = "Niton XL5", deviceLod = 3.0, deviceLodUnit = "ppm",
            } },
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("Ba", Assert.Single((await store.GetConstraintsAsync("proj-1"))!.ElementPools).Element);
    }

    [Fact]
    public async Task Confirm_IsA404_WhenTheProjectHasNoConstraintsYet()
    {
        // Constraints are written by the intake agent. No constraints means intake has not run, and
        // there is no component list to validate a component name against.
        using var app = NewApp(new InMemoryRecordStore());
        var res = await app.CreateClient().PostAsJsonAsync(
            "/projects/nope/xrf/confirm", new { proposals = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsWhatIsAlreadyConfirmed_SoTheScreenCanShowIt()
    {
        var store = await StoreWithProject();
        var c = (await store.GetConstraintsAsync("proj-1"))!;
        c.ElementPools = [new("bottle", "Ba", "Ka", "V")];
        await store.UpsertConstraintsAsync(c);
        using var app = NewApp(store);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/projects/proj-1/xrf");

        Assert.Equal("Ba", body.GetProperty("elementPools")[0].GetProperty("element").GetString());
        Assert.Equal("bottle", body.GetProperty("components")[0].GetString());
    }

    [Fact]
    public async Task Template_IsServedAsADownloadableCsv_MatchingTheParsersColumns()
    {
        using var app = NewApp(new InMemoryRecordStore());
        var res = await app.CreateClient().GetAsync("/xrf-template.csv");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.StartsWith(string.Join(",", XrfTemplate.Columns), text);
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheXrfSurface()
    {
        // A missing [FromServices] on any store parameter breaks routing for the WHOLE app, and that
        // failure shows up nowhere else.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
