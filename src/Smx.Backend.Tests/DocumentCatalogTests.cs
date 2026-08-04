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
        int attempts = 0, string? masterId = null, string? nextAttempt = null) =>
        new(masterId ?? $"{element}_{form}", element, form, cas, status,
            attempts > 0 ? "2026-07-18T00:00:00Z" : null, attempts, nextAttempt);

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

    /// A failed row now names WHEN it will be tried again.
    ///
    /// Before 2026-07-29 a row that had burned its three attempts went to `awaiting_operator`, and the
    /// subtitle read "awaiting operator upload — no automated source" — which was, on the day it was
    /// written, true, and which the redesign makes a lie in both halves: there IS an automated source,
    /// and the system will chase it on its own. Backoff means nothing is terminal, so the honest
    /// subtitle is a date.
    [Fact]
    public async Task AFailedGapNamesItsNextAttempt()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 3,
            nextAttempt: "2026-08-15T03:00:00Z"));

        var row = Assert.Single(await Provider.ListAsync());

        Assert.Contains("2026-08-15", row.Subtitle);
        Assert.Contains("3 fetch attempt", row.Subtitle);   // the diagnostic survives as a diagnostic
        Assert.DoesNotContain("awaiting operator", row.Subtitle);
    }

    /// The scheduler is the source of the date, and it may not have stamped one yet (a row failed by
    /// an older build, or written between the migration and the first sweep). Saying "soon" is not a
    /// hedge — it is the only true thing available, and inventing a date would be worse.
    [Fact]
    public async Task AFailedGapWithNoScheduledRetrySaysSo()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 1));

        var row = Assert.Single(await Provider.ListAsync());

        Assert.Contains("1 fetch attempt", row.Subtitle);
        Assert.DoesNotContain("next attempt ", row.Subtitle);
    }

    /// `awaiting_operator` is deleted, and a migration rewrites the rows that hold it — but a read can
    /// land before that migration runs, and the catalog must not render a raw status token at the
    /// operator. The legacy branch is kept deliberately, and it now tells the truth about the new
    /// world: the sweep will retry this on its own.
    [Fact]
    public async Task ALegacyAwaitingOperatorRowStillRendersSensibly()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "awaiting_operator", attempts: 3));

        var row = Assert.Single(await Provider.ListAsync());

        Assert.DoesNotContain("awaiting_operator", row.Subtitle);           // never the raw token
        Assert.DoesNotContain("no automated source", row.Subtitle);         // no longer true
        Assert.Contains("retry", row.Subtitle, StringComparison.OrdinalIgnoreCase);
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
        var row = Assert.Single(await Provider.ListAsync());   // Single, not rows[0] — a second fixture
                                                                 // row added later must not silently
                                                                 // move this assertion onto the wrong row
        Assert.Equal(DocumentStates.Superseded, row.State);
        Assert.True(row.Available);      // superseded still opens — it is history, not absence
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

    // Sds/Triggers/OperatorUpload.cs validates only Cas and PdfBase64 — a blank RevisionDate reaches
    // here as a registry id with an empty trailing segment ("cas|supplier|"). DocumentId.Encode
    // doesn't validate the payload shape, so this used to look exactly like a healthy row in the
    // list and then 404 with no explanation on click. It must be visibly broken from the list itself.
    [Fact]
    public async Task MarksASheetWithAnUnresolvableIdAsUnavailableRatherThanHidingIt()
    {
        _source.Sheets.Add(Sheet("7440-22-4", "sigma", ""));   // blank RevisionDate -> "7440-22-4|sigma|"

        var row = Assert.Single(await Provider.ListAsync());

        Assert.False(row.Available);
        Assert.Equal(DocumentStates.Missing, row.State);
        Assert.Contains(UnavailableReasons.UnresolvableId, row.Subtitle);
        Assert.Contains("7440-22-4|sigma|", row.Subtitle);          // names the malformed id, not just that it's broken
        Assert.False(DocumentId.TryDecode(row.Id, out _, out _));   // honest: this id will never resolve
    }

    // The gap side has the same exposure: SdsMasterRow.Id is `{element}_{form}` (DedupKey.ForMasterList)
    // and a blank Form produces the same empty-segment shape.
    [Fact]
    public async Task MarksAGapRowWithAnUnresolvableIdAsUnavailableRatherThanHidingIt()
    {
        _source.Master.Add(new SdsMasterRow("Nd_", "Nd", "", "1313-97-9", "pending", null, 0));

        var row = Assert.Single(await Provider.ListAsync());

        Assert.False(row.Available);
        Assert.Equal(DocumentStates.Missing, row.State);
        Assert.Contains(UnavailableReasons.UnresolvableId, row.Subtitle);
        Assert.False(DocumentId.TryDecode(row.Id, out _, out _));
    }

    // The real invariant BLOCKING 2 exists to guarantee: nothing the catalog lists can look healthy
    // and then silently fail to open. Every row either decodes (so GetAsync can find it) or is
    // honest, right there in the list, that it can't.
    [Fact]
    public async Task EveryListedRowEitherResolvesOrIsMarkedUnavailable()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2024-03-11"));   // healthy
        _source.Sheets.Add(Sheet("7440-22-4", "alfa", ""));              // malformed: blank revision
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 2));

        var rows = await Provider.ListAsync();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(DocumentId.TryDecode(r.Id, out _, out _) || !r.Available));
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
    // application/xhtml+xml contains BOTH "html" and "xml" as substrings ("x-html"-"+xml") — the
    // csv → json → xml → html → pdf branch order (mirroring BronzeIngestor.ExtensionFor exactly)
    // is what makes this resolve to "xml", matching what the ingestor actually wrote. Four curated
    // EUR-Lex sources (reach-annex-xvii, clp-annex-vi, eu-pops, eu-rohs) send this Accept header —
    // enabled:false today, so a wrong branch order here is a latent 404 waiting for one of them to
    // be turned on.
    [InlineData("application/xhtml+xml", "raw.xml")]
    // A real Content-Type header, not the bare MIME type — the ".Contains" matching must survive it.
    [InlineData("text/html; charset=utf-8", "raw.html")]
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

    // The terminal fallback matters, not just the branch order: BronzeIngestor falls back to the
    // fetched URL's own extension when the content type is generic, and a source served as
    // application/octet-stream with a plainly-.csv URL is common and reachable today — building
    // "raw.bin" for it is a path the writer never wrote.
    [Fact]
    public async Task FallsBackToTheCuratedDocUrlsExtensionWhenTheContentTypeIsGeneric()
    {
        _source.Sources.Add(new RegSourceRow("oehha", "Prop 65", "OEHHA",
            [new RegDocTitleRow("list", "https://oehha.ca.gov/media/downloads/list.csv", "Prop 65 list")]));
        _source.Docs.Add(new RegDocRow("list", "oehha", "sha", "2025-01-01", "run", "20260701T000000Z"));
        _bronze.Put("regulatory/oehha/list/20260701T000000Z/meta.json",
            """
            {"sourceId":"oehha","docId":"list","sourceUrl":"https://oehha.ca.gov/media/downloads/list.csv",
             "officialDate":"2025-01-01","fetchTs":"20260701T000000Z","sha256":"sha",
             "contentType":"application/octet-stream","httpStatus":200,"syncRunId":"run"}
            """);
        var id = DocumentId.Encode(DocumentId.Reg, "oehha/list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.Equal("regulatory/oehha/list/20260701T000000Z/raw.csv", detail!.BlobPath);
    }

    // Spec §3 invariant 6: a missing sidecar yields "not recorded", never an invented value. This is
    // the exact list of what publishing looks like with no meta.json — not merely "no empty
    // strings" (Assert.NotEqual("", ...)), which "application/octet-stream" (an internal guess used
    // only to pick a file extension) passes right through. That gap is what let BLOCKING 3 ship: the
    // guess was being published as the "Content type" row and as Summary.ContentType.
    [Fact]
    public async Task StatesNotRecordedWhenTheSidecarIsAbsent()
    {
        GivenSyncedSource();
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail!.Summary.ContentType);
        Assert.Contains(detail.Provenance, p => p.Label == "Source URL" && p.Value == "not recorded");
        Assert.Contains(detail.Provenance, p => p.Label == "SHA-256" && p.Value == "not recorded");
        Assert.Contains(detail.Provenance, p => p.Label == "Content type" && p.Value == "not recorded");
        Assert.Contains(detail.Provenance, p => p.Label == "HTTP status" && p.Value == "not recorded");
        // These four come from the reg-state row itself, not the sidecar — real recorded facts, not
        // guesses, so they are NOT "not recorded" even with meta.json absent.
        Assert.Contains(detail.Provenance, p => p.Label == "Authority" && p.Value == "ECHA");
        Assert.Contains(detail.Provenance, p => p.Label == "Official date" && p.Value == "2025-11-20");
        Assert.Contains(detail.Provenance, p => p.Label == "Fetched" && p.Value == "20260701T031400Z");
        Assert.Contains(detail.Provenance, p => p.Label == "Sync run" && p.Value == "sync-2026-07-01-a3f");
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

    // A `reg`-kinded id whose sourceId is NOT curated is a hand-edited id: the row it names is real
    // (it exists in reg-state, e.g. because it was seed-imported), but a synced-layout path built from
    // it — regulatory/{sourceId}/{docId}/{fetchTs}/ — was never written, because that sourceId never
    // went through the synced ingest path. Refuse rather than construct a path that cannot exist.
    [Fact]
    public async Task RefusesARegKindedIdWhoseSourceIdIsNotCurated()
    {
        _source.Docs.Add(new RegDocRow("clp-annex-vi", "eu", "abc123", "2024-08-14", "seed-run", "2026-07-05"));
        var id = DocumentId.Encode(DocumentId.Reg, "eu/clp-annex-vi");

        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
        Assert.Empty(_bronze.PathsRead);
    }

    // The mirror case: a `seed`-kinded id whose sourceId IS curated. The row is real, but the seed
    // layout — seed/{region}/{docId}/, no fetchTs segment — was never written for a curated source;
    // that source is synced. Same refusal, same reason: never guess a path into existence.
    [Fact]
    public async Task RefusesASeedKindedIdWhoseSourceIdIsCurated()
    {
        GivenSyncedSource();
        var id = DocumentId.Encode(DocumentId.Seed, "echa-svhc/candidate-list");

        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
        Assert.Empty(_bronze.PathsRead);
    }

    // LastFetchTs is a reg-state field, not something DocumentId ever validates, and it becomes a
    // folder segment for a `reg` doc. Not reachable through the public API today — DocumentId already
    // gates the incoming id, and this value never left the state store — but the class's own header
    // comment names DocumentId as THE boundary, and this is the one place that builds a path from a
    // value DocumentId had no chance to see. It must be refused on its own, same as any other
    // traversal attempt, rather than trusted because it came from Cosmos instead of the wire.
    [Theory]
    [InlineData("../../../sds/some-other-doc")]
    [InlineData("20260701T031400Z/../../secret")]
    [InlineData("nested/path")]
    public async Task RefusesALastFetchTsThatContainsAPathSeparatorOrTraversal(string poisonedFetchTs)
    {
        _source.Sources.Add(new RegSourceRow("echa-svhc", "REACH SVHC", "ECHA",
            [new RegDocTitleRow("candidate-list", "https://echa.europa.eu/candidate-list", "SVHC candidate list")]));
        _source.Docs.Add(new RegDocRow("candidate-list", "echa-svhc", "9f2c1ae4", "2025-11-20",
            "sync-2026-07-01-a3f", poisonedFetchTs));
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
        Assert.Empty(_bronze.PathsRead);
    }
}

public class DocumentCatalogTests
{
    private readonly InMemorySdsDocumentSource _sds = new();
    private readonly InMemoryRegDocumentSource _reg = new();
    private readonly InMemoryDocumentContentStore _bronze = new();
    private readonly InMemoryCoaDocumentSource _coa = new();

    private DocumentCatalog Catalog => new(
        new SdsDocumentProvider(_sds), new RegDocumentProvider(_reg, _bronze), new CoaDocumentProvider(_coa));

    private void Given()
    {
        _sds.Sheets.Add(new SdsSheetRow("7761-88-8|sigma|2024-03-11", "7761-88-8", "sigma", "Silver nitrate",
            "2024-03-11", "EU", "en", "https://x.test/a.pdf", "sds/7761-88-8/sigma/2024-03-11.pdf", true,
            "2026-07-16T00:00:00Z", null, null));
        _sds.Master.Add(new SdsMasterRow("Nd_oxide", "Nd", "oxide", "1313-97-9", "failed", "2026-07-18T00:00:00Z", 3));
        _reg.Sources.Add(new RegSourceRow("echa-svhc", "REACH SVHC", "ECHA",
            // "substance list", not "candidate list" — the latter's "ca-ND-idate" would collide with
            // the "Nd" search-term test case below and falsely match two rows instead of one.
            [new RegDocTitleRow("candidate-list", "https://echa.europa.eu/cl", "SVHC substance list")]));
        _reg.Docs.Add(new RegDocRow("candidate-list", "echa-svhc", "9f", "2025-11-20", "run", "20260701T031400Z"));
    }

    [Fact]
    public async Task ListsBothHalves()
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter());
        Assert.Equal(3, rows.Count);   // 1 sheet + 1 gap + 1 regulation
    }

    [Theory]
    [InlineData(DocumentKinds.Sds, 2)]    // the sheet and the gap
    [InlineData(DocumentKinds.Reg, 1)]
    [InlineData(DocumentKinds.Seed, 0)]
    [InlineData(DocumentKinds.All, 3)]
    public async Task FiltersByKind(string kind, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(Kind: kind));
        Assert.Equal(expected, rows.Count);
    }

    [Theory]
    [InlineData(DocumentStates.Available, 2)]
    [InlineData(DocumentStates.Missing, 1)]
    [InlineData(DocumentStates.All, 3)]
    public async Task FiltersByState(string state, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(State: state));
        Assert.Equal(expected, rows.Count);
    }

    [Theory]
    [InlineData("silver", 1)]
    [InlineData("7761", 1)]
    [InlineData("svhc", 1)]
    [InlineData("Nd", 1)]
    [InlineData("nothing-matches-this", 0)]
    public async Task FiltersBySearchAcrossTitleAndSubtitle(string q, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(Q: q));
        Assert.Equal(expected, rows.Count);
    }

    [Fact]
    public async Task RoutesGetToTheOwningProvider()
    {
        Given();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json",
            """
            {"contentType":"text/html","sha256":"9f","sourceUrl":"https://echa.europa.eu/cl",
             "fetchTs":"20260701T031400Z","syncRunId":"run","httpStatus":200}
            """);

        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.Sds, "7761-88-8|sigma|2024-03-11")));
        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide")));
        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list")));
    }

    // Spec §3 invariant 2: a malformed id must not reach storage at all. Asserting 'null' alone
    // would not distinguish "rejected" from "resolved and missed". Three genuinely distinct failure
    // modes, not one repeated: not base64 at all; no kind prefix (no '_') to even look up; and a
    // WELL-FORMED "reg_" id whose decoded payload trips the dedicated ".." check specifically — the
    // previous third case (a bare base64 blob with no kind prefix) was really case 2 again, since
    // base64url's own '_' characters just make it fail the unknown-kind lookup the same way.
    [Fact]
    public async Task AMalformedIdNeverTouchesStorage()
    {
        Given();
        Assert.Null(await Catalog.GetAsync("sds_!!!!"));
        Assert.Null(await Catalog.GetAsync("../../etc/passwd"));
        Assert.Null(await Catalog.GetAsync("reg_" + DocumentId.EncodePayload("echa-svhc/../../secret")));
        Assert.Empty(_bronze.PathsRead);
    }

    // The policy (DocumentCatalog's OrderBy chain) is: missing rows first within a kind, then
    // alphabetically by title. Calling ListAsync twice and comparing the two results — the previous
    // version of this test — passes even with the entire OrderBy chain deleted, as long as the fakes
    // themselves iterate in a fixed order; it never actually exercises the policy. This inserts rows
    // in an order that agrees with NEITHER "missing first" nor "alphabetical" and asserts the exact
    // resulting sequence, so a deleted or reordered sort clause fails it.
    [Fact]
    public async Task OrdersMissingRowsFirstThenAlphabeticallyWithinKind()
    {
        _sds.Sheets.Add(new SdsSheetRow("7440-21-3|acme|2024-01-01", "7440-21-3", "acme", "Silver nitrate",
            "2024-01-01", "EU", "en", "https://x.test/s.pdf", "sds/silver.pdf", true,
            "2026-07-16T00:00:00Z", null, null));
        _sds.Sheets.Add(new SdsSheetRow("7664-93-9|acme|2024-01-01", "7664-93-9", "acme", "Aardvark acid",
            "2024-01-01", "EU", "en", "https://x.test/a.pdf", "sds/aardvark.pdf", true,
            "2026-07-16T00:00:00Z", null, null));
        _sds.Master.Add(new SdsMasterRow("Nd_oxide", "Nd", "oxide", "1313-97-9", "failed", "2026-07-18T00:00:00Z", 3));

        var rows = await Catalog.ListAsync(new DocumentFilter());

        Assert.Equal(
            ["Nd oxide — no safety sheet", "Aardvark acid", "Silver nitrate"],
            rows.Select(r => r.Title));
    }
}
