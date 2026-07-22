namespace Smx.Domain.Documents;

/// Routes a document to the store that holds its chunks: regulatory and seeded documents to
/// reg-silver, safety sheets to the sds-index. Gap rows have no chunks by definition.
public sealed class CompositeDocumentTextReader(IDocumentTextReader regulatory, IDocumentTextReader sds)
    : IDocumentTextReader
{
    public Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!document.Summary.Available) return Task.FromResult<IReadOnlyList<DocumentChunk>>([]);
        return document.Summary.Kind switch
        {
            DocumentKinds.Reg or DocumentKinds.Seed => regulatory.ReadChunksAsync(document, ct),
            DocumentKinds.Sds => sds.ReadChunksAsync(document, ct),
            _ => Task.FromResult<IReadOnlyList<DocumentChunk>>([]),
        };
    }
}
