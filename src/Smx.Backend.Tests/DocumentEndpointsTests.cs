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
    private const string SheetBlob = "sds/7761-88-8/sigma/2024-03-11.pdf";

    private void GivenASheet(string title = "Silver nitrate") => _sds.Sheets.Add(new SdsSheetRow(
        SheetId, "7761-88-8", "sigma", title, "2024-03-11", "EU", "en",
        "https://x.test/a.pdf", SheetBlob, true, "2026-07-16T00:00:00Z", null, null));

    /// The sheet's bytes. Separate from GivenASheet because a registry row WITHOUT its blob is a real
    /// state — that is the drift the detail endpoint has to name.
    private void GivenItsBlob(string content = "%PDF-1.4 hello") => _bronze.Put(SheetBlob, content);

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

    // An unrecognised filter value must not read as "no documents". This whole feature exists so
    // absence never passes for coverage, and a typo'd ?kind= answering 200 [] is that same failure
    // in miniature — the operator sees an empty library and concludes there is nothing there.
    [Theory]
    [InlineData("/documents?kind=bogus")]
    [InlineData("/documents?kind=sdsgap")]      // an ID kind, deliberately NOT a facet
    [InlineData("/documents?state=pending")]    // a master-list status, not a catalog state
    public async Task List_400sOnAFilterValueItDoesNotKnow(string url)
    {
        GivenASheet(); GivenAGap();
        var res = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task List_AcceptsEveryFilterValueItDocuments()
    {
        GivenASheet(); GivenAGap();
        foreach (var url in new[]
                 {
                     "/documents", "/documents?kind=all", "/documents?kind=sds", "/documents?kind=reg",
                     "/documents?kind=seed", "/documents?state=all", "/documents?state=available",
                     "/documents?state=missing", "/documents?state=superseded", "/documents?kind=&state=",
                 })
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Detail_ReturnsProvenance()
    {
        GivenASheet(); GivenItsBlob();
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

        // Both branches of the detail endpoint, plus the list: the drift branch is the one that
        // KNOWS the path is wrong and so is the one most tempted to quote it.
        var drifted = await _client.GetStringAsync($"/documents/{id}");
        GivenItsBlob();
        var resolved = await _client.GetStringAsync($"/documents/{id}");
        var list = await _client.GetStringAsync("/documents");

        foreach (var body in new[] { drifted, resolved, list })
        {
            Assert.DoesNotContain("blobPath", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sds/7761-88-8/sigma", body, StringComparison.Ordinal);
        }
    }

    // Spec §3 invariant 6: what was not recorded says so. RegistryPointer.Region and .Language are
    // both nullable at the writer, so an SDS whose sheet never carried them rendered a bare " / " —
    // which reads as a value, not as an absence, on a traceability rail.
    [Fact]
    public async Task Detail_AnUnrecordedRegionOrLanguageSaysSo()
    {
        _sds.Sheets.Add(new SdsSheetRow(
            SheetId, "7761-88-8", "sigma", "Silver nitrate", "2024-03-11", null!, null!,
            "https://x.test/a.pdf", SheetBlob, true, "2026-07-16T00:00:00Z", null, null));
        GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");

        var line = json.GetProperty("provenance").EnumerateArray()
            .Single(p => p.GetProperty("label").GetString() == "Region / language");
        Assert.Equal("not recorded", line.GetProperty("value").GetString());
        Assert.DoesNotContain(" / ", json.GetProperty("summary").GetProperty("subtitle").GetString()!);
    }

    // One half recorded and the other not is the more common shape, and the half that IS known must
    // survive rather than being flattened into a single "not recorded".
    [Fact]
    public async Task Detail_AHalfRecordedRegionKeepsTheHalfItHas()
    {
        _sds.Sheets.Add(new SdsSheetRow(
            SheetId, "7761-88-8", "sigma", "Silver nitrate", "2024-03-11", "EU", null!,
            "https://x.test/a.pdf", SheetBlob, true, "2026-07-16T00:00:00Z", null, null));
        GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");

        var line = json.GetProperty("provenance").EnumerateArray()
            .Single(p => p.GetProperty("label").GetString() == "Region / language");
        Assert.Equal("EU / not recorded", line.GetProperty("value").GetString());
    }

    // The drift check asks storage a yes/no question, and must ask it as one. Reading the blob to
    // answer it pulls the whole document — 20 MB of egress and a large-object allocation per detail
    // view of a big PDF — and then discards every byte.
    [Fact]
    public async Task Detail_DriftCheckAsksWhetherTheBlobExists_WithoutReadingIt()
    {
        GivenASheet(); GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");

        Assert.True(json.GetProperty("summary").GetProperty("available").GetBoolean());
        Assert.Contains(SheetBlob, _bronze.PathsRead);      // it did consult storage...
        Assert.Equal(0, _bronze.ContentReads);              // ...without fetching a byte of the document
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

    [Fact]
    public async Task Content_StreamsTheStoredBytesWithSafetyHeaders()
    {
        GivenASheet(); GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-1.4 hello", await res.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        // The CSP sandbox directive makes the browser treat the response as sandboxed however it is
        // framed — belt to the frontend's braces, which never same-origins this content anyway.
        Assert.Contains("sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("inline", res.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Content_DownloadFlagSwitchesToAttachment()
    {
        GivenASheet(); GivenItsBlob("%PDF");
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content?download=1");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.False(string.IsNullOrEmpty(res.Content.Headers.ContentDisposition.FileNameStar
                                          ?? res.Content.Headers.ContentDisposition.FileName));
    }

    // Registry says the sheet exists, storage disagrees. That is real drift and it gets its own
    // reason on the detail endpoint rather than a bare error.
    [Fact]
    public async Task Content_404sWhenTheBlobIsMissing()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");
        Assert.Equal("blob-missing", detail.GetProperty("unavailableReason").GetString());
    }

    // A gap id is VALID and its document is knowably absent — neither 404 nor 500. 409 says
    // "this exists as a record and has no file", which is exactly the state.
    [Fact]
    public async Task Content_409sForAGapRow()
    {
        GivenAGap();
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var content = await _client.GetAsync($"/documents/{id}/content");
        var text = await _client.GetAsync($"/documents/{id}/text");
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, text.StatusCode);
    }

    [Fact]
    public async Task Text_ReturnsChunksInOrder()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        _text.Chunks[id] =
        [
            new DocumentChunk(0, "Section 1 identification", null, "1"),
            new DocumentChunk(1, "Section 2 hazards", null, "2"),
        ];

        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}/text");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("Section 1 identification", json[0].GetProperty("text").GetString());
    }

    // Spec §8: a document in bronze that never reached the index is a real and important state.
    // An empty array is the honest answer; the viewer says what it means.
    [Fact]
    public async Task Text_ReturnsEmptyArrayWhenNothingWasIndexed()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}/text");
        Assert.Equal(0, json.GetArrayLength());
    }

    // This endpoint does NOT serve byte ranges, and says so: a Range header is answered with the whole
    // document, 200. It used to pass enableRangeProcessing:true, which is a no-op on the only stream
    // production ever hands it — FileStreamHttpResult derives length from stream.CanSeek, and the ADLS
    // read stream cannot seek — so the old 206 assertion passed only because this fake's MemoryStream
    // can. A browser that asks for a range and receives the whole body renders it fine; a test that
    // claims a capability production does not have is the actual hazard.
    //
    // What must survive either way are the safety headers, because they are set by a filter that runs
    // before the result writes anything.
    [Fact]
    public async Task Content_ARangeRequestGetsTheWholeDocument_StillWithTheSafetyHeaders()
    {
        GivenASheet(); GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/documents/{id}/content");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("%PDF-1.4 hello", await res.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("inline", res.Content.Headers.ContentDisposition!.DispositionType);
    }

    // The title is Cosmos data, not URL input, but it is still not our string: it reaches a response
    // header and then the operator's filesystem.
    [Fact]
    public async Task Content_DownloadName_SurvivesATitleThatSanitisesToNothingUseful()
    {
        GivenASheet(title: "../.."); GivenItsBlob();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var dots = (await _client.GetAsync($"/documents/{id}/content?download=1"))
            .Content.Headers.ContentDisposition!;
        Assert.Equal("document.pdf", dots.FileName!.Trim('"'));

        _sds.Sheets.Clear();
        GivenASheet(title: "CON");
        var device = (await _client.GetAsync($"/documents/{id}/content?download=1"))
            .Content.Headers.ContentDisposition!;
        Assert.Equal("document-CON.pdf", device.FileName!.Trim('"'));
    }

    [Fact]
    public async Task ContentAndText_404ForMalformedIds()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/documents/sds_!!!!/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/documents/sds_!!!!/text")).StatusCode);
        Assert.Empty(_bronze.PathsRead);
    }
}
