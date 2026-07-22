using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// SDS chunks from the `sds-index` AI Search index.
///
/// blobPath is NOT filterable on that index (SdsSearchClient.cs:31), so the filter is the dedup
/// triple — cas + supplier + revisionDate — which is exactly DedupKey.ForRegistry and therefore
/// identifies one sheet. Ordering comes from the ordinal encoded in each chunk key, since the index
/// carries no ordinal field.
public sealed class SdsIndexTextReader(SearchClient sdsIndex) : IDocumentTextReader
{
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? Content { get; set; }
        public string? GhsSection { get; set; }
    }

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Sds) return [];

        var parts = DocumentId.SegmentsOf(kind, payload);
        var (cas, supplier, revisionDate) = (parts[0], parts[1], parts[2]);

        var options = new SearchOptions
        {
            Filter = $"cas eq '{Escape(cas)}' and supplier eq '{Escape(supplier)}' and revisionDate eq '{Escape(revisionDate)}'",
            Size = 1000,
        };
        options.Select.Add("id");
        options.Select.Add("content");
        options.Select.Add("ghsSection");

        var response = await sdsIndex.SearchAsync<Row>("*", options, ct);
        var rows = new List<Row>();
        await foreach (var hit in response.Value.GetResultsAsync().WithCancellation(ct))
            if (hit.Document is not null) rows.Add(hit.Document);

        return rows.OrderBy(r => SdsChunkOrdinal.From(r.Id))
                   .Select(r => new DocumentChunk(SdsChunkOrdinal.From(r.Id), r.Content ?? "", null, r.GhsSection))
                   .ToList();
    }

    /// OData string literals escape a single quote by doubling it.
    private static string Escape(string value) => value.Replace("'", "''");
}
