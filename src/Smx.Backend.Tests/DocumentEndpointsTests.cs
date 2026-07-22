using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain.Documents;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class DocumentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemorySdsDocumentSource _sds = new();
    private readonly InMemoryRegDocumentSource _reg = new();
    private readonly InMemoryDocumentContentStore _bronze = new();
    private readonly InMemoryDocumentTextReader _text = new();
    private readonly HttpClient _client;

    public DocumentEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<ISdsDocumentSource>(_sds);
                s.AddSingleton<IRegDocumentSource>(_reg);
                s.AddSingleton<IDocumentContentStore>(_bronze);
                s.AddSingleton<IDocumentTextReader>(_text);
                s.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(
                    new SdsDocumentProvider(sp.GetRequiredService<ISdsDocumentSource>()),
                    new RegDocumentProvider(sp.GetRequiredService<IRegDocumentSource>(),
                                            sp.GetRequiredService<IDocumentContentStore>())));
            })).CreateClient();
    }

    private const string SheetId = "7761-88-8|sigma|2024-03-11";

    private void GivenASheet() => _sds.Sheets.Add(new SdsSheetRow(
        SheetId, "7761-88-8", "sigma", "Silver nitrate", "2024-03-11", "EU", "en",
        "https://x.test/a.pdf", "sds/7761-88-8/sigma/2024-03-11.pdf", true, "2026-07-16T00:00:00Z", null, null));

    private void GivenAGap() => _sds.Master.Add(new SdsMasterRow(
        "Nd_oxide", "Nd", "oxide", "1313-97-9", "failed", "2026-07-18T00:00:00Z", 3));

    [Fact]
    public async Task List_ReturnsEmptyArrayOnColdStart()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/documents");
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task List_ReturnsSheetsAndGaps()
    {
        GivenASheet(); GivenAGap();
        var json = await _client.GetFromJsonAsync<JsonElement>("/documents");
        Assert.Equal(2, json.GetArrayLength());
    }

    [Fact]
    public async Task List_FiltersByStateAndQuery()
    {
        GivenASheet(); GivenAGap();
        var missing = await _client.GetFromJsonAsync<JsonElement>("/documents?state=missing");
        Assert.Equal(1, missing.GetArrayLength());
        var q = await _client.GetFromJsonAsync<JsonElement>("/documents?q=silver");
        Assert.Equal(1, q.GetArrayLength());
    }

    [Fact]
    public async Task Detail_ReturnsProvenance()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");
        Assert.True(json.GetProperty("summary").GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("provenance").GetArrayLength() > 0);
    }

    // The blob path is an internal detail. Leaking it to the client would hand back exactly the
    // string the id scheme exists to keep out of the API surface.
    [Fact]
    public async Task Detail_NeverLeaksTheBlobPath()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var body = await _client.GetStringAsync($"/documents/{id}");
        Assert.DoesNotContain("blobPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sds/7761-88-8/sigma", body, StringComparison.Ordinal);
    }

    // Spec §8: a gap row is 200-with-a-reason, not 404. It is a known absence, not a lookup failure.
    [Fact]
    public async Task Detail_OfAGapRow_Is200WithAStatedReason()
    {
        GivenAGap();
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var res = await _client.GetAsync($"/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("summary").GetProperty("available").GetBoolean());
        Assert.Equal("never-fetched", json.GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public async Task Detail_404sForUnknownAndMalformedIds()
    {
        foreach (var id in new[] { DocumentId.Encode(DocumentId.Sds, "0-0|nobody|1999-01-01"), "sds_!!!!", "garbage" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/documents/{id}")).StatusCode);
    }

    // Spec §3 invariant 2: rejection happens before storage is consulted.
    [Fact]
    public async Task AMalformedIdNeverReachesStorage()
    {
        await _client.GetAsync("/documents/sds_!!!!");
        await _client.GetAsync("/documents/nope_YWJj");
        Assert.Empty(_bronze.PathsRead);
    }
}
