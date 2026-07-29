using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Documents;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Tools;

namespace Smx.Backend.Tests;

public class KnowledgeEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly InMemorySdsCorpusReader _corpus = new();
    private readonly RecordingSdsAcquisition _sds = new();
    private readonly HttpClient _client;

    public KnowledgeEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IKnowledgeStore>(_knowledge);
                s.AddSingleton<ISdsCorpusReader>(_corpus);
                s.AddSingleton<ISdsAcquisition>(_sds);
            })).CreateClient();
    }

    /// A local fake rather than a shared one: the fetch passthrough is the only thing in this suite
    /// that touches acquisition, and what it needs to prove is that the endpoint hands the CAS across
    /// and hands the answer back unaltered.
    private sealed class RecordingSdsAcquisition : ISdsAcquisition
    {
        public List<string> Ensured { get; } = [];
        public List<SdsUpload> Uploaded { get; } = [];
        public SdsEnsureResult Next { get; set; } = new(SdsEnsureStatus.Fetched);
        public SdsUploadResult NextUpload { get; set; } = new(true, RegistryId: "cas|acme|2026-01-01");

        public Task<SdsEnsureResult> EnsureAsync(string cas, string? element, string? form, CancellationToken ct)
        {
            Ensured.Add(cas);
            return Task.FromResult(Next);
        }

        public Task AppendAsync(string element, string form, string cas, CancellationToken ct) => Task.CompletedTask;

        public Task<SdsUploadResult> UploadAsync(SdsUpload upload, CancellationToken ct)
        {
            Uploaded.Add(upload);
            return Task.FromResult(NextUpload);
        }
    }

    private static MultipartFormDataContent UploadForm(byte[] pdf, string? supplier, string? revisionDate)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "sheet.pdf");
        if (supplier is not null) form.Add(new StringContent(supplier), "supplier");
        if (revisionDate is not null) form.Add(new StringContent(revisionDate), "revisionDate");
        return form;
    }

    [Fact]
    public async Task GetMarkerLibrary_ReturnsMatches_AndEmptyArrayOnColdStart()
    {
        var empty = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anything");
        Assert.Equal(0, empty.GetArrayLength());

        await _knowledge.UpsertMarkerAsync(new MarkerLibraryDoc
        {
            Id = KnowledgeIds.Marker("m1"), Composition = new(["Zr"], 200, "1:0"),
            ValidatedFor = new("anti-counterfeit", "label", "overt"), SourceProject = "p1", CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anti-counterfeit");
        Assert.Equal(1, hit.GetArrayLength());
    }

    [Fact]
    public async Task GetLearnedConclusions_FiltersBySearch()
    {
        await _knowledge.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Material, "zr|bottle"), Kind = KnowledgeKinds.Material,
            Scope = new("Zr", null, "bottle", null, null, null), Finding = "Zr neodecanoate preferred.",
            Confidence = 0.9, Provenance = new(["p1"], []), CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=neodecanoate");
        Assert.Equal(1, hit.GetArrayLength());
        var miss = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=cadmium");
        Assert.Equal(0, miss.GetArrayLength());
    }

    /// D8: the review signature is gone, and so is the route that recorded it. Pinned rather than
    /// merely deleted — a 404 here is the difference between "we removed the button" and "we removed
    /// the gate's second, contradictory predicate".
    [Fact]
    public async Task Msds_ReviewRoute_IsGone()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7", Supplier = "Acme", Version = "3", Date = "2025-01-01",
        });

        var gone = await _client.PostAsJsonAsync("/msds-registry/13463-67-7/review", new { });

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// The passthrough behind "Fetch now". Thin on purpose: the whole value of the answer is in the
    /// diagnostics, so the endpoint must not summarise, translate or swallow them.
    [Fact]
    public async Task PostMsdsFetch_HandsTheCasToAcquisition_AndTheAnswerBackVerbatim()
    {
        _sds.Next = new SdsEnsureResult(SdsEnsureStatus.Unavailable,
            Reason: "no candidate validated",
            Attempted: [new SdsAttempt("https://x/y.pdf", "Acme", "rejected: CAS not in document")]);

        var res = await _client.PostAsync("/msds/1310-73-2/fetch", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);      // "unavailable" is an ANSWER, not a failure
        Assert.Equal(["1310-73-2"], _sds.Ensured);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SdsEnsureStatus.Unavailable, body.GetProperty("status").GetString());
        Assert.Equal("no candidate validated", body.GetProperty("reason").GetString());
        // What was tried and why each one failed — the point of the contract.
        var attempt = Assert.Single(body.GetProperty("attempted").EnumerateArray().ToList());
        Assert.Equal("rejected: CAS not in document", attempt.GetProperty("outcome").GetString());
    }

    /// The fallback that has never existed. It is a fallback and not an override — the file still goes
    /// through the same content validation a fetched sheet faces, which is why the endpoint's job is
    /// only to carry it there intact.
    [Fact]
    public async Task PostMsdsUpload_CarriesTheFileToAcquisition()
    {
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };   // %PDF-

        var res = await _client.PostAsync("/msds/1310-73-2/upload",
            UploadForm(pdf, "Acme Chemicals", "2026-05-01"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var up = Assert.Single(_sds.Uploaded);
        Assert.Equal("1310-73-2", up.Cas);
        Assert.Equal("Acme Chemicals", up.Supplier);
        Assert.Equal("2026-05-01", up.RevisionDate);
        Assert.Equal(pdf, up.Pdf);
        // No product name given: the CAS labels the row rather than a blank.
        Assert.Equal("1310-73-2", up.ProductName);
    }

    /// Supplier and revision date are two thirds of the registry's compound key. A sheet stored without
    /// them gets an id with an empty segment, which DocumentId.TryDecode refuses — ingested, listed,
    /// and permanently un-openable. Refusing costs two fields; accepting costs the document.
    [Theory]
    [InlineData(null, "2026-05-01")]
    [InlineData("Acme Chemicals", null)]
    [InlineData("   ", "2026-05-01")]
    public async Task PostMsdsUpload_RefusesASheetThatCouldNeverBeOpenedAgain(string? supplier, string? revisionDate)
    {
        var res = await _client.PostAsync("/msds/1310-73-2/upload",
            UploadForm([0x25, 0x50, 0x44, 0x46], supplier, revisionDate));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty(_sds.Uploaded);          // nothing was stored — a refusal, not a partial write
    }

    /// A rejected upload IS this endpoint's failure: the operator handed us a file and the answer is
    /// that this file will not do. That belongs on the browser's failure path, with the reason intact.
    [Fact]
    public async Task PostMsdsUpload_RelaysTheValidatorsRejection()
    {
        _sds.NextUpload = new SdsUploadResult(false, "rejected: CAS not in document");

        var res = await _client.PostAsync("/msds/1310-73-2/upload",
            UploadForm([0x25, 0x50, 0x44, 0x46], "Acme Chemicals", "2026-05-01"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("CAS not in document", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetMsds_BrowsesAll()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d",
        });
        var all = await _client.GetFromJsonAsync<JsonElement>("/msds-registry");
        Assert.Equal(1, all.GetArrayLength());
    }

    // ---- compose-at-read: the SDS corpus is the source of sheet facts; msds-registry is a
    // governance overlay (review signature). Design §6.3: reference the corpus, don't copy it. ----

    private static SdsCorpusSheet Sheet(string cas, string supplier, string rev, string ingested = "2026-07-16T12:00:00Z")
        => new(cas, supplier, "product", rev, ingested);

    [Fact]
    public async Task GetMsds_ListsCorpusSheets()
    {
        _corpus.Sheets.Add(Sheet("1313-97-9", "Stanford Advanced Materials", "2022-11-02"));

        var rows = await _client.GetFromJsonAsync<List<MsdsRegistryDoc>>("/msds-registry", Json.Options);

        var row = Assert.Single(rows!);
        Assert.Equal("1313-97-9", row.Cas);
        Assert.Equal("Stanford Advanced Materials", row.Supplier);
        Assert.Equal("2022-11-02", row.Date);
    }

    /// LinkedProjects is the overlay's ONLY remaining job, and — unlike the signature it replaced —
    /// it does not lapse when a newer revision arrives. "Project P put this substance in play" stays
    /// true across revisions, so the merge is by CAS alone while sheet facts still come from the sheet.
    [Fact]
    public async Task GetMsds_OverlaysLinkedProjects_EvenOnANewerRevisionThanTheOverlayNames()
    {
        _corpus.Sheets.Add(Sheet("200-00-0", "Beta", "2026-03-01"));
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("200-00-0"), Cas = "200-00-0", Supplier = "Acme", Version = "",
            Date = "2026-02-01", LinkedProjects = ["p1", "p2"],   // an OLDER revision than the corpus holds
        });

        var rows = (await _client.GetFromJsonAsync<List<MsdsRegistryDoc>>("/msds-registry", Json.Options))!;

        var row = Assert.Single(rows);
        Assert.Equal(["p1", "p2"], row.LinkedProjects);
        Assert.Equal("2026-03-01", row.Date);      // the sheet's revision, never the overlay's
        Assert.Equal("Beta", row.Supplier);        // the sheet's supplier, never the overlay's
    }

    [Fact]
    public async Task GetMsds_PicksLatestSheetPerCas_AndKeepsGovernanceOnlyRows()
    {
        _corpus.Sheets.Add(Sheet("300-00-0", "Acme", "2025-01-01"));
        _corpus.Sheets.Add(Sheet("300-00-0", "Beta", "2026-01-01"));
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc      // manual/legacy row, not in corpus
        {
            Id = KnowledgeIds.Msds("999-99-9"), Cas = "999-99-9", Supplier = "Manual", Version = "1", Date = "d",
        });

        var rows = (await _client.GetFromJsonAsync<List<MsdsRegistryDoc>>("/msds-registry", Json.Options))!;

        Assert.Equal(2, rows.Count);
        var corpusRow = rows.Single(r => r.Cas == "300-00-0");
        Assert.Equal("Beta", corpusRow.Supplier);                 // latest revision wins
        Assert.Equal("2026-01-01", corpusRow.Date);
        Assert.Contains(rows, r => r.Cas == "999-99-9");          // governance-only row stays visible
    }

    /// The registry screen gates procurement, so it must be able to OPEN the sheet behind a row.
    /// The id is served, not derived by the caller: re-implementing DedupKey's normalisation in the
    /// browser would put the same rule in a second language, where drift shows up as a silent 404.
    [Fact]
    public async Task GetMsds_CarriesTheDocumentIdOfTheSheetBehindEachRow()
    {
        _corpus.Sheets.Add(Sheet("7761-88-8", " Sigma-Aldrich ", "2024-03-11"));
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc      // governance-only: no sheet exists
        {
            Id = KnowledgeIds.Msds("999-99-9"), Cas = "999-99-9", Supplier = "Manual", Version = "1", Date = "d",
        });

        var rows = await _client.GetFromJsonAsync<JsonElement>("/msds-registry");
        var byCas = rows.EnumerateArray().ToDictionary(r => r.GetProperty("cas").GetString()!);

        var id = byCas["7761-88-8"].GetProperty("documentId").GetString()!;
        Assert.True(DocumentId.TryDecode(id, out var kind, out var payload));
        Assert.Equal(DocumentId.Sds, kind);
        // The payload is the sds-registry id: DedupKey.ForRegistry, trimmed and lowercased.
        Assert.Equal("7761-88-8|sigma-aldrich|2024-03-11", payload);

        // Null is omitted by Json.Options. A row with no sheet behind it must not offer a link.
        Assert.False(byCas["999-99-9"].TryGetProperty("documentId", out _));
    }

    /// A corpus row missing a supplier or a revision date makes a registry id with an empty
    /// segment, which TryDecode refuses. Serving it anyway would put a 404 behind a link on the
    /// screen that blocks orders; withholding it leaves the row visible and honest instead.
    [Fact]
    public async Task GetMsds_OmitsTheDocumentId_WhenTheSheetsIdWouldNotResolve()
    {
        _corpus.Sheets.Add(Sheet("500-00-0", "", "2026-01-01"));

        var rows = await _client.GetFromJsonAsync<JsonElement>("/msds-registry");
        var row = Assert.Single(rows.EnumerateArray().ToList());

        Assert.Equal("500-00-0", row.GetProperty("cas").GetString());
        Assert.False(row.TryGetProperty("documentId", out _));
    }
}
