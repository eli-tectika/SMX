namespace Smx.Domain.Documents;

/// The certificate-of-analysis half of the catalog, assembled straight from the bronze container.
///
/// There is no registry behind it — see CoaRow. So unlike SdsDocumentProvider there are no gap rows:
/// a COA that was never obtained is not catalogued anywhere, and this surface cannot invent it. That
/// asymmetry is honest rather than convenient. A missing SAFETY sheet has a known shape (the master
/// list says which substance needs one) and blocks an order, so its absence is a first-class row; a
/// missing COA has no such shape yet, and a list of empty placeholders would be a list of guesses.
public sealed class CoaDocumentProvider(ICoaDocumentSource source)
{
    /// Where the loader writes them. One constant, because a provider reading a different prefix from
    /// the one the loader writes would produce an empty library and look exactly like "none uploaded".
    public const string Prefix = "customer-documents/coa";

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var rows = await source.ListAsync(ct);
        return rows
            // A file whose name cannot round-trip through DocumentId gets no row at all. Publishing an
            // id the detail endpoint would then refuse is the failure CanEncode exists to catch — and
            // a row that 404s on click is worse than one that was never offered.
            .Where(r => DocumentId.CanEncode(DocumentId.Coa, r.FileName))
            .Select(Summarize)
            .ToList();
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;
        if (kind != DocumentId.Coa) return null;

        var row = await source.GetAsync(payload, ct);
        if (row is null) return null;

        var provenance = new List<ProvenanceField>
        {
            new("Origin", "operator upload (customer document drop)"),
            new("File", row.FileName),
            new("Size", $"{row.SizeBytes:N0} bytes"),
            new("Last modified", row.LastModifiedUtc is { Length: > 0 } ? row.LastModifiedUtc : "not recorded"),
            // Named explicitly rather than left blank. A COA carries the assayed composition that
            // Background and Dosing would want, and none of it has been extracted yet — saying so
            // beats a rail that simply shows nothing and reads as "there is nothing to know".
            new("Assay extraction", "not yet extracted — the file is stored, its contents are not indexed"),
        };

        return new DocumentDetail(Summarize(row), provenance, null, null, null, row.BlobPath);
    }

    private static DocumentSummary Summarize(CoaRow row) => new(
        Id: DocumentId.Encode(DocumentId.Coa, row.FileName),
        Kind: DocumentKinds.Coa,
        // The filename is the only title there is. Parsing a supplier or a lot number out of it would
        // be right for these eight files and wrong for the ninth, and a certificate attributed to the
        // wrong supplier is exactly the kind of confident-and-wrong the citation chips refuse to be.
        Title: Path.GetFileNameWithoutExtension(row.FileName),
        Subtitle: "certificate of analysis · supplier document",
        Available: true,
        State: DocumentStates.Available,
        ContentType: ContentTypeFor(row.FileName),
        OfficialDate: null,
        IngestedUtc: row.LastModifiedUtc);

    /// Only the extensions the drop actually contains, and null for anything else — the viewer must
    /// not be told a type that was never established (spec §3 invariant 6).
    private static string? ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            _ => null,
        };
}
