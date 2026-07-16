using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class KnowledgeEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly InMemorySdsCorpusReader _corpus = new();
    private readonly HttpClient _client;

    public KnowledgeEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IKnowledgeStore>(_knowledge);
                s.AddSingleton<ISdsCorpusReader>(_corpus);
            })).CreateClient();
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

    [Fact]
    public async Task Msds_Review_FlipsStatus_And404ForUnknown()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7", Supplier = "Acme", Version = "3", Date = "2025-01-01",
        });
        var ok = await _client.PostAsJsonAsync("/msds-registry/13463-67-7/review", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var reviewed = (await _knowledge.GetMsdsAsync("13463-67-7"))!;
        Assert.Equal(MsdsReviewStatus.Reviewed, reviewed.ReviewStatus);
        // A gate record must carry when it was signed, not just that it was.
        Assert.NotNull(reviewed.ReviewedAt);
        Assert.InRange(DateTimeOffset.Parse(reviewed.ReviewedAt!), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

        var missing = await _client.PostAsJsonAsync("/msds-registry/nope/review", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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
    public async Task GetMsds_ListsCorpusSheets_DefaultingToUnreviewed()
    {
        _corpus.Sheets.Add(Sheet("1313-97-9", "Stanford Advanced Materials", "2022-11-02"));

        var rows = await _client.GetFromJsonAsync<List<MsdsRegistryDoc>>("/msds-registry", Json.Options);

        var row = Assert.Single(rows!);
        Assert.Equal("1313-97-9", row.Cas);
        Assert.Equal("Stanford Advanced Materials", row.Supplier);
        Assert.Equal("2022-11-02", row.Date);
        Assert.Equal(MsdsReviewStatus.Unreviewed, row.ReviewStatus);
    }

    [Fact]
    public async Task GetMsds_OverlaysReview_OnlyWhenItSignedTheCurrentRevision()
    {
        _corpus.Sheets.Add(Sheet("100-00-0", "Acme", "2026-01-01"));
        _corpus.Sheets.Add(Sheet("200-00-0", "Acme", "2026-03-01"));
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc      // signed THE current revision
        {
            Id = KnowledgeIds.Msds("100-00-0"), Cas = "100-00-0", Supplier = "Acme", Version = "",
            Date = "2026-01-01", ReviewStatus = MsdsReviewStatus.Reviewed, ReviewedAt = "2026-02-01T00:00:00Z",
        });
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc      // signed an OLDER revision
        {
            Id = KnowledgeIds.Msds("200-00-0"), Cas = "200-00-0", Supplier = "Acme", Version = "",
            Date = "2026-02-01", ReviewStatus = MsdsReviewStatus.Reviewed, ReviewedAt = "2026-02-02T00:00:00Z",
        });

        var rows = (await _client.GetFromJsonAsync<List<MsdsRegistryDoc>>("/msds-registry", Json.Options))!;

        Assert.Equal(MsdsReviewStatus.Reviewed, rows.Single(r => r.Cas == "100-00-0").ReviewStatus);
        // A newer sheet arrived after the signature: the review must NOT silently bless it.
        var stale = rows.Single(r => r.Cas == "200-00-0");
        Assert.Equal(MsdsReviewStatus.Unreviewed, stale.ReviewStatus);
        Assert.Equal("2026-03-01", stale.Date);
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

    [Fact]
    public async Task Msds_Review_CreatesTheGovernanceDocFromTheCorpus_WhenMissing()
    {
        _corpus.Sheets.Add(Sheet("1313-97-9", "Stanford Advanced Materials", "2022-11-02"));

        var ok = await _client.PostAsJsonAsync("/msds-registry/1313-97-9/review", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var doc = (await _knowledge.GetMsdsAsync("1313-97-9"))!;
        Assert.Equal(MsdsReviewStatus.Reviewed, doc.ReviewStatus);
        Assert.Equal("Stanford Advanced Materials", doc.Supplier);
        Assert.Equal("2022-11-02", doc.Date);                     // the signature names the revision it signed
        Assert.NotNull(doc.ReviewedAt);
    }
}
