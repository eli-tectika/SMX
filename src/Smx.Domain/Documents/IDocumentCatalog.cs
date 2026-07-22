namespace Smx.Domain.Documents;

public interface IDocumentCatalog
{
    Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentFilter filter, CancellationToken ct = default);
    Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default);
}
