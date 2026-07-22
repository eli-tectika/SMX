namespace Smx.Domain.Documents;

/// The chunks an agent actually retrieved — returned VERBATIM. No re-extraction, no cleanup, no
/// re-chunking (spec §3 invariant 4). If the index holds garbage, the operator must see the garbage;
/// that is the entire reason this surface exists.
public interface IDocumentTextReader
{
    Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default);
}
