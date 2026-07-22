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
