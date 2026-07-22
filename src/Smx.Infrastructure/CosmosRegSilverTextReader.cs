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
///
/// **Only `live` chunks.** reg-silver keeps three generations per docId (CosmosRegStores.PromoteAsync):
/// `staged` is fetched-but-not-yet-promoted, `live` is current, `superseded` is the previous revision
/// kept for audit. Only `live` was ever pushed to the Gold AI Search index, so only `live` is what an
/// agent could actually have retrieved. Without this filter a re-synced document renders all three
/// generations interleaved by chunkIndex — text the agent never saw, presented on the one surface
/// whose entire purpose is showing what it did see. That is worse than showing nothing.
public sealed class CosmosRegSilverTextReader(Container regSilver) : IDocumentTextReader
{
    private const string LiveStatus = "live";

    private sealed record Row(int ChunkIndex, string Text, CitationRow? Citation);
    private sealed record CitationRow(string? EntryId, string? ArticleOrAnnex);

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Reg && kind != DocumentId.Seed) return [];
        var docId = DocumentId.SegmentsOf(kind, payload)[1];

        var q = new QueryDefinition(
                "SELECT c.chunkIndex, c.text, c.citation FROM c WHERE c.docId = @docId AND c.status = @status")
            .WithParameter("@docId", docId)
            .WithParameter("@status", LiveStatus);

        var rows = new List<Row>();
        using var it = regSilver.GetItemQueryIterator<Row>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(docId) });
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));

        return rows.OrderBy(r => r.ChunkIndex)
                   .Select(r => new DocumentChunk(r.ChunkIndex, r.Text, r.Citation?.EntryId, r.Citation?.ArticleOrAnnex))
                   .ToList();
    }
}
