using System.Text.Json;
using Azure.Core.Serialization;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// SDS chunks from the `sds-index` AI Search index.
///
/// The sheet is identified by the dedup triple — cas + supplier + revisionDate — which is exactly
/// DedupKey.ForRegistry and therefore identifies one sheet. blobPath would be the obvious filter but
/// is not filterable on that index (SdsSearchClient.cs:31). Ordering comes from the ordinal encoded
/// in each chunk key, since the index carries no ordinal field.
///
/// Only `cas` is filtered SERVER-side. The index stores the raw ingest metadata while the document id
/// stores DedupKey's NORMALISED form, and AI Search `eq` is exact and case-sensitive — so
/// `supplier eq 'sigma-aldrich'` can never match the stored "Sigma-Aldrich", and every supplier in
/// the allowlist is mixed-case. CAS numbers are digits and hyphens, where normalisation is the
/// identity, so an exact match on that one field is safe; supplier and revisionDate are matched
/// client-side through SdsRegistryKey, which normalises both sides. See SdsRegistryKey for the
/// full account.
public sealed class SdsIndexTextReader(SearchClient sdsIndex) : IDocumentTextReader
{
    /// The options the production SearchClient MUST be constructed with. Exposed here — rather than
    /// left to the composition root to remember — so the test can assert against the SAME serializer
    /// the client will actually use, which is the only version of that assertion worth making.
    ///
    /// Azure.Search.Documents defaults to PropertyNamingPolicy = null and PropertyNameCaseInsensitive
    /// = false, so a camelCase index binds NOTHING into a PascalCase POCO: every field lands null,
    /// silently, and the viewer renders "no agent has read this document". The orchestrator's search
    /// tools escape this only because they deserialize into Dictionary&lt;string, object&gt;; Row is
    /// the estate's first typed search POCO, so there was no precedent to inherit. Web defaults are
    /// what Smx.Functions/Program.cs:79-82 already configures on the write side.
    public static SearchClientOptions ClientOptions() => new()
    {
        Serializer = new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    /// Internal, not private: SdsIndexTextReaderTests round-trips this exact type through the
    /// serializer above. A test-local copy of it would prove nothing about what production binds to.
    internal sealed class Row
    {
        public string Id { get; set; } = "";
        public string? Content { get; set; }
        public string? GhsSection { get; set; }
        public string? Cas { get; set; }
        public string? Supplier { get; set; }
        public string? RevisionDate { get; set; }
    }

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Sds) return [];

        // The payload IS the registry id (DocumentId's Shapes table: cas | supplier | revisionDate,
        // already normalised), so a reconstructed key can be compared against it directly.
        var cas = DocumentId.SegmentsOf(kind, payload)[0];

        var options = new SearchOptions { Filter = $"cas eq '{Escape(cas)}'", Size = 1000 };
        options.Select.Add("id");
        options.Select.Add("content");
        options.Select.Add("ghsSection");
        // Selected to be matched client-side, not for display.
        options.Select.Add("cas");
        options.Select.Add("supplier");
        options.Select.Add("revisionDate");

        var response = await sdsIndex.SearchAsync<Row>("*", options, ct);
        var rows = new List<Row>();
        await foreach (var hit in response.Value.GetResultsAsync().WithCancellation(ct))
            if (hit.Document is not null) rows.Add(hit.Document);

        return rows.Where(r => SdsRegistryKey.ForRegistry(r.Cas, r.Supplier, r.RevisionDate) == payload)
                   .OrderBy(r => SdsChunkOrdinal.From(r.Id))
                   .Select(r => new DocumentChunk(SdsChunkOrdinal.From(r.Id), r.Content ?? "", null, r.GhsSection))
                   .ToList();
    }

    /// OData string literals escape a single quote by doubling it.
    private static string Escape(string value) => value.Replace("'", "''");
}
