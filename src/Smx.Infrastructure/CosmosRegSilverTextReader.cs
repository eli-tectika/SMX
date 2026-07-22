using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// Regulatory chunks from `reg-silver` (PK /docId). A point-partition query — the whole document's
/// chunks live in one partition — ordered by chunkIndex.
///
/// Returned VERBATIM (spec §3 invariant 4). Each chunk carries its own citation.entryId, which is
/// what the viewer anchors a citation to.
///
/// camelCase property names in the SELECT: these documents are written by the regsync Functions app
/// with camelCase serialisation, and a PascalCase projection silently returns nulls.
public sealed class CosmosRegSilverTextReader(Container regSilver) : IDocumentTextReader
{
    private sealed record Row(int ChunkIndex, string Text, CitationRow? Citation);
    private sealed record CitationRow(string? EntryId, string? ArticleOrAnnex);

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Reg && kind != DocumentId.Seed) return [];
        var docId = DocumentId.SegmentsOf(kind, payload)[1];

        var q = new QueryDefinition(
                "SELECT c.chunkIndex, c.text, c.citation FROM c WHERE c.docId = @docId")
            .WithParameter("@docId", docId);

        var rows = new List<Row>();
        using var it = regSilver.GetItemQueryIterator<Row>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(docId) });
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));

        return rows.OrderBy(r => r.ChunkIndex)
                   .Select(r => new DocumentChunk(r.ChunkIndex, r.Text, r.Citation?.EntryId, r.Citation?.ArticleOrAnnex))
                   .ToList();
    }
}
