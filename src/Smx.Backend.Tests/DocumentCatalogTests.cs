using Smx.Domain.Documents;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class SdsDocumentProviderTests
{
    private readonly InMemorySdsDocumentSource _source = new();
    private SdsDocumentProvider Provider => new(_source);

    private static SdsSheetRow Sheet(string cas, string supplier, string rev, string? superseded = null,
        string? masterId = null) =>
        new($"{cas}|{supplier}|{rev}", cas, supplier, $"{supplier} {cas}", rev, "EU", "en",
            $"https://example.test/{cas}.pdf", $"sds/{cas}/{supplier}/{rev}.pdf", true,
            "2026-07-16T00:00:00Z", superseded, masterId);

    private static SdsMasterRow Master(string element, string form, string cas, string status,
        int attempts = 0, string? masterId = null) =>
        new(masterId ?? $"{element}_{form}", element, form, cas, status, attempts > 0 ? "2026-07-18T00:00:00Z" : null, attempts);

    [Fact]
    public async Task ListsSheetsAsAvailable()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2024-03-11"));
        var rows = await Provider.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(DocumentKinds.Sds, row.Kind);
        Assert.True(row.Available);
        Assert.Equal(DocumentStates.Available, row.State);
        Assert.Equal("application/pdf", row.ContentType);
        Assert.Contains("7761-88-8", row.Subtitle);
    }

    // Design D9: a missing MSDS is the thing that blocks an order. Listing only files that exist
    // would let absence read as coverage.
    [Fact]
    public async Task EmitsGapRowsForSubstancesWithNoSheet()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 3));
        var rows = await Provider.ListAsync();
        var row = Assert.Single(rows);
        Assert.False(row.Available);
        Assert.Equal(DocumentStates.Missing, row.State);
        Assert.Equal(DocumentKinds.Sds, row.Kind);          // facet is sds — a MISSING sheet is still a sheet
        Assert.StartsWith("sdsgap_", row.Id);                // but the id resolves against a different container
    }

    // A substance that HAS a sheet must not also appear as a gap; otherwise every fetched substance
    // is listed twice and the "missing" count is meaningless.
    [Fact]
    public async Task DoesNotEmitAGapForASubstanceThatHasASheet()
    {
        _source.Sheets.Add(Sheet("1313-97-9", "alfa", "2025-02-02", masterId: "Nd_oxide"));
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "fetched"));
        var rows = await Provider.ListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Available);
    }

    // The link may be by masterListId OR, for older rows that predate it, by CAS.
    [Fact]
    public async Task SuppressesTheGapWhenOnlyTheCasMatches()
    {
        _source.Sheets.Add(Sheet("1313-97-9", "alfa", "2025-02-02", masterId: null));
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "pending"));
        var rows = await Provider.ListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Available);
    }

    [Fact]
    public async Task MarksSupersededSheets()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2023-01-01", superseded: "7761-88-8|sigma|2024-03-11"));
        var rows = await Provider.ListAsync();
        Assert.Equal(DocumentStates.Superseded, rows[0].State);
        Assert.True(rows[0].Available);      // superseded still opens — it is history, not absence
    }

    [Fact]
    public async Task ResolvesASheetToItsBlobPathAndProvenance()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2024-03-11"));
        var id = DocumentId.Encode(DocumentId.Sds, "7761-88-8|sigma|2024-03-11");
        var detail = await Provider.GetAsync(id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("sds/7761-88-8/sigma/2024-03-11.pdf", detail!.BlobPath);
        Assert.Contains(detail.Provenance, p => p.Label == "Source URL" && p.Kind == ProvenanceKinds.Url);
        Assert.Contains(detail.Provenance, p => p.Label == "Supplier" && p.Value == "sigma");
        Assert.Contains(detail.Provenance, p => p.Label == "Revision date" && p.Value == "2024-03-11");
    }

    // Spec §8: a gap row is not a lookup failure. It resolves, reports why there is no file, and
    // carries the attempt count that tells the operator whether retrying is worth anything.
    [Fact]
    public async Task ResolvesAGapRowToAStatedAbsence()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 3));
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var detail = await Provider.GetAsync(id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.False(detail!.Summary.Available);
        Assert.Equal(UnavailableReasons.NeverFetched, detail.UnavailableReason);
        Assert.Null(detail.BlobPath);
        Assert.NotNull(detail.UnavailableDetail);
        Assert.Contains("3", detail.UnavailableDetail);
    }

    [Fact]
    public async Task ReturnsNullForAnUnknownSheet()
    {
        var id = DocumentId.Encode(DocumentId.Sds, "0000-00-0|nobody|1999-01-01");
        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
    }
}

public class RegDocumentProviderTests
{
    private readonly InMemoryRegDocumentSource _source = new();
    private readonly InMemoryDocumentContentStore _bronze = new();
    private RegDocumentProvider Provider => new(_source, _bronze);

    private const string Meta = """
        {"sourceId":"echa-svhc","docId":"candidate-list","sourceUrl":"https://echa.europa.eu/candidate-list",
         "officialDate":"2025-11-20","fetchTs":"20260701T031400Z","sha256":"9f2c1ae4",
         "contentType":"text/html","httpStatus":200,"syncRunId":"sync-2026-07-01-a3f"}
        """;

    private void GivenSyncedSource()
    {
        _source.Sources.Add(new RegSourceRow("echa-svhc", "REACH SVHC", "ECHA",
            [new RegDocTitleRow("candidate-list", "https://echa.europa.eu/candidate-list", "SVHC candidate list")]));
        _source.Docs.Add(new RegDocRow("candidate-list", "echa-svhc", "9f2c1ae4", "2025-11-20",
            "sync-2026-07-01-a3f", "20260701T031400Z"));
    }

    // A reg-state row whose sourceId matches a curated source is a SYNCED document; the path carries
    // the fetch timestamp as a folder. Classification happens here, at catalog time — never by
    // probing storage for which prefix happens to exist.
    [Fact]
    public async Task SyncedDocumentsResolveUnderTheRegulatoryPrefix()
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json", Meta);
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("regulatory/echa-svhc/candidate-list/20260701T031400Z/raw.html", detail!.BlobPath);
        Assert.Equal("text/html", detail.Summary.ContentType);
    }

    // A reg-state row whose sourceId is NOT a curated source came from the seed importer, where
    // sourceId is the region and the path has no fetchTs segment at all (SeedImporter.cs:96,109).
    [Fact]
    public async Task SeededDocumentsResolveUnderTheSeedPrefixWithNoTimestampSegment()
    {
        _source.Docs.Add(new RegDocRow("clp-annex-vi", "eu", "abc123", "2024-08-14", "seed-run", "2026-07-05"));
        _bronze.Put("seed/eu/clp-annex-vi/meta.json",
            """
            {"sourceId":"eu","docId":"clp-annex-vi","sourceUrl":"https://eur-lex.europa.eu/clp",
             "officialDate":"2024-08-14","fetchTs":"2026-07-05","sha256":"abc123",
             "contentType":"text/plain","httpStatus":0,"syncRunId":"seed-run"}
            """);
        var id = DocumentId.Encode(DocumentId.Seed, "eu/clp-annex-vi");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("seed/eu/clp-annex-vi/raw.txt", detail!.BlobPath);
    }

    [Fact]
    public async Task ListClassifiesEachDocByRegistryMembership()
    {
        GivenSyncedSource();
        _source.Docs.Add(new RegDocRow("clp-annex-vi", "eu", "abc123", "2024-08-14", "seed-run", "2026-07-05"));

        var rows = await Provider.ListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == DocumentKinds.Reg && r.Id.StartsWith("reg_"));
        Assert.Contains(rows, r => r.Kind == DocumentKinds.Seed && r.Id.StartsWith("seed_"));
    }

    // The title comes from the curated registry when there is one; a seeded doc has only its id.
    [Fact]
    public async Task UsesTheCuratedTitleWhenTheRegistryHasOne()
    {
        GivenSyncedSource();
        var rows = await Provider.ListAsync();
        Assert.Equal("SVHC candidate list", rows[0].Title);
    }

    // The extension is not stored anywhere — it is derived at ingest from the content type and then
    // discarded. meta.json is where it comes back from, and the same read populates the rail.
    [Theory]
    [InlineData("text/html", "raw.html")]
    [InlineData("application/pdf", "raw.pdf")]
    [InlineData("text/csv", "raw.csv")]
    [InlineData("application/json", "raw.json")]
    [InlineData("application/xml", "raw.xml")]
    [InlineData("application/octet-stream", "raw.bin")]
    public async Task DerivesTheExtensionFromTheStoredContentType(string contentType, string expectedFile)
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json",
            $$"""
            {"sourceId":"echa-svhc","docId":"candidate-list","sourceUrl":"https://x.test",
             "officialDate":"2025-11-20","fetchTs":"20260701T031400Z","sha256":"9f",
             "contentType":"{{contentType}}","httpStatus":200,"syncRunId":"r"}
            """);
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.EndsWith(expectedFile, detail!.BlobPath);
    }

    // Spec §3 invariant 6: a missing sidecar yields "not recorded", never an invented value.
    [Fact]
    public async Task StatesNotRecordedWhenTheSidecarIsAbsent()
    {
        GivenSyncedSource();
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains(detail!.Provenance, p => p.Label == "SHA-256" && p.Value == "not recorded");
        Assert.All(detail.Provenance, p => Assert.NotEqual("", p.Value));
    }

    [Fact]
    public async Task PopulatesTheRailFromTheSidecar()
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json", Meta);
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.Contains(detail!.Provenance, p => p.Label == "SHA-256" && p.Value == "9f2c1ae4");
        Assert.Contains(detail.Provenance, p => p.Label == "Sync run" && p.Value == "sync-2026-07-01-a3f");
        Assert.Contains(detail.Provenance, p => p.Label == "Authority" && p.Value == "ECHA");
    }

    [Fact]
    public async Task ReturnsNullForAnUnknownDoc()
    {
        var id = DocumentId.Encode(DocumentId.Reg, "nope/nothing");
        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
    }
}
