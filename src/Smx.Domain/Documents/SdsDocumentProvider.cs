namespace Smx.Domain.Documents;

/// The safety-data-sheet half of the catalog: `sds-registry` (sheets that exist) unioned with the
/// rows of `sds-master-list` that are not already covered by a sheet — substances the system knows
/// it needs a sheet for and does not have.
///
/// "Covered" is judged by actual linkage to a registry row (masterListId, falling back to CAS — see
/// ListAsync), not by trusting the master row's own `Status` text. A `fetched` status with no
/// matching sheet is real drift (a deleted registry row, a broken link) and must still surface as a
/// gap; the point of D9 is that the catalog reports what it can actually open, not what a status
/// field claims.
///
/// Emitting the gaps is deliberate (design D9). A missing MSDS is exactly what blocks an order, so a
/// library that listed only files would let absence read as coverage.
public sealed class SdsDocumentProvider(ISdsDocumentSource source)
{
    public const string SheetContentType = "application/pdf";

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var sheets = await source.ListSheetsAsync(ct);
        var master = await source.ListMasterAsync(ct);

        var rows = sheets.Select(ToSummary).ToList();

        // Suppress the gap for anything already covered by a sheet. The link is masterListId where
        // the ingest recorded one, and CAS otherwise — older registry rows predate masterListId.
        var coveredMasterIds = sheets.Where(s => s.MasterListId is { Length: > 0 })
                                     .Select(s => s.MasterListId!)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredCas = sheets.Select(s => s.Cas).ToHashSet(StringComparer.OrdinalIgnoreCase);

        rows.AddRange(master
            .Where(m => !coveredMasterIds.Contains(m.Id) && !coveredCas.Contains(m.Cas))
            .Select(m => ToGapSummary(m, Explain(m))));

        return rows;
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;

        if (kind == DocumentId.Sds)
        {
            var sheet = await source.GetSheetAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            return sheet is null ? null : new DocumentDetail(
                ToSummary(sheet),
                Provenance(sheet),
                UnavailableReason: null,
                UnavailableDetail: null,
                SupersededById: sheet.SupersededBy is { Length: > 0 }
                    ? DocumentId.Encode(DocumentId.Sds, sheet.SupersededBy) : null,
                BlobPath: sheet.BlobPath);
        }

        if (kind == DocumentId.SdsGap)
        {
            var m = await source.GetMasterAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            if (m is null) return null;

            var explanation = Explain(m);
            return new DocumentDetail(
                ToGapSummary(m, explanation),
                [
                    new("CAS", m.Cas),
                    new("Element", m.Element),
                    new("Form", m.Form),
                    new("Status", m.Status),
                    new("Fetch attempts", m.AttemptCount.ToString()),
                    new("Last attempt", m.LastAttemptUtc ?? "not recorded"),
                ],
                UnavailableReason: UnavailableReasons.NeverFetched,
                UnavailableDetail: explanation,
                SupersededById: null,
                BlobPath: null);
        }

        return null;
    }

    private static DocumentSummary ToSummary(SdsSheetRow s) => new(
        Id: DocumentId.Encode(DocumentId.Sds, s.Id),
        Kind: DocumentKinds.Sds,
        Title: string.IsNullOrWhiteSpace(s.ProductName) ? s.Cas : s.ProductName,
        Subtitle: $"CAS {s.Cas} · {s.Supplier} · rev {s.RevisionDate} · {s.Region} / {s.Language}",
        Available: true,
        State: s.SupersededBy is { Length: > 0 } ? DocumentStates.Superseded : DocumentStates.Available,
        ContentType: SheetContentType,
        OfficialDate: s.RevisionDate,
        IngestedUtc: s.IngestedUtc);

    private static DocumentSummary ToGapSummary(SdsMasterRow m, string explanation) => new(
        Id: DocumentId.Encode(DocumentId.SdsGap, m.Id),
        Kind: DocumentKinds.Sds,          // facet: a missing sheet is still a sheet
        Title: $"{m.Element} {m.Form} — no safety sheet",
        Subtitle: $"CAS {m.Cas} · {explanation}",
        Available: false,
        State: DocumentStates.Missing,
        ContentType: null,
        OfficialDate: null,
        IngestedUtc: null);

    private static IReadOnlyList<ProvenanceField> Provenance(SdsSheetRow s) =>
    [
        new("Source URL", s.SourceUrl, ProvenanceKinds.Url),
        new("Supplier", s.Supplier),
        new("Product name", string.IsNullOrWhiteSpace(s.ProductName) ? "not recorded" : s.ProductName),
        new("CAS", s.Cas),
        new("Revision date", s.RevisionDate),
        new("Region / language", $"{s.Region} / {s.Language}"),
        new("Ingested", s.IngestedUtc),
        new("Indexed", s.Indexed ? "yes" : "no"),
        new("Superseded by", s.SupersededBy is { Length: > 0 } ? s.SupersededBy : "—"),
    ];

    private static string Explain(SdsMasterRow m) => m.Status switch
    {
        "failed" => $"{m.AttemptCount} fetch attempt(s) failed · last {m.LastAttemptUtc ?? "not recorded"}",
        "awaiting_operator" => "awaiting operator upload — no automated source",
        "pending" => "queued for fetch",
        _ => m.Status,
    };
}
