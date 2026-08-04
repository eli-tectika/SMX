namespace Smx.Domain.Documents;

/// Composes the two providers and applies the list filters.
///
/// Assembled on read, never stored (design D3). Both registries are small — nine curated regulatory
/// sources and a per-substance sheet list — and a stored projection would mean touching both ingest
/// pipelines, backfilling everything already written, and keeping a second copy of state that can
/// disagree with the blob store. When the corpus outgrows this, IDocumentCatalog is the seam: the
/// implementation changes and neither the API nor the UI notices.
public sealed class DocumentCatalog(SdsDocumentProvider sds, RegDocumentProvider reg, CoaDocumentProvider coa)
    : IDocumentCatalog
{
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentFilter filter, CancellationToken ct = default)
    {
        var rows = new List<DocumentSummary>();
        if (filter.Kind is DocumentKinds.All or DocumentKinds.Sds)
            rows.AddRange(await sds.ListAsync(ct));
        if (filter.Kind is DocumentKinds.All or DocumentKinds.Reg or DocumentKinds.Seed)
            rows.AddRange(await reg.ListAsync(ct));
        if (filter.Kind is DocumentKinds.All or DocumentKinds.Coa)
            rows.AddRange(await coa.ListAsync(ct));

        IEnumerable<DocumentSummary> q = rows;

        if (filter.Kind != DocumentKinds.All)
            q = q.Where(r => r.Kind == filter.Kind);

        if (filter.State != DocumentStates.All)
            q = q.Where(r => r.State == filter.State);

        if (!string.IsNullOrWhiteSpace(filter.Q))
            q = q.Where(r =>
                r.Title.Contains(filter.Q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(filter.Q, StringComparison.OrdinalIgnoreCase));

        // Stable order, or the library reshuffles under the operator on every refresh. Missing rows
        // sort first inside their kind: the gaps are the actionable ones.
        return q.OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Available ? 1 : 0)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out _)) return null;
        return kind switch
        {
            DocumentId.Sds or DocumentId.SdsGap => await sds.GetAsync(documentId, ct),
            DocumentId.Reg or DocumentId.Seed => await reg.GetAsync(documentId, ct),
            DocumentId.Coa => await coa.GetAsync(documentId, ct),
            _ => null,
        };
    }
}
