using System.Text;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// The write side of the `learned-conclusions` AI Search index — the retrievable projection of the
/// Cosmos learned-conclusions container (Cosmos stays authoritative). Mirrors
/// Smx.Functions/Reg/Ingestion/RegSearchClient: the index is created in code on first push, because
/// AI Search indexes have no ARM/Bicep resource type (data-plane; the workload identity holds
/// Search Index Data Contributor). ILearnedConclusionsSearch is the read side.
public sealed class LearnedConclusionsIndex : ILearnedConclusionsIndex
{
    private const int VectorDims = 3072; // text-embedding-3-large — see BackendOptions.EmbeddingDeployment
    private const string VectorProfile = "lc-hnsw";
    private const string VectorAlgo = "lc-hnsw-config";

    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;

    public LearnedConclusionsIndex(SearchIndexClient indexClient, string indexName)
    { _indexClient = indexClient; _indexName = indexName; }

    /// The schema. Every field name here MUST equal a [JsonPropertyName] on LearnedConclusionChunk —
    /// that pairing is the whole contract between writer and index, and LearnedConclusionsIndexSchemaTests
    /// asserts it in both directions. A field the chunk does not emit is dead; a chunk property with no
    /// field is dropped on push.
    ///
    /// Public + static so the schema is assertable without an Azure endpoint.
    public static SearchIndex BuildIndex(string name)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },

            // MUST stay named "content" and MUST stay searchable. LearnedConclusionsSearchTool reads exactly
            // `id` and `content` off a hit and maps them to RetrievedChunk. Rename this field or drop
            // IsSearchable and every retrieval silently returns nothing — which the tool reports to the agent
            // as "no prior conclusions", indistinguishable from a genuinely empty knowledge layer. Not an error.
            // A failure this quiet in a system whose job is picking taggants is the worst kind.
            //
            // ANALYZER — deliberately the default (standard.lucene), deliberately NOT a language analyzer.
            // NEVER change this to en.lucene / en.microsoft, and do not "improve" it.
            // The English stopword lists contain as, at, be, in, no, it, is, or, so, to — which are the symbols
            // for arsenic (As), astatine (At), beryllium (Be), indium (In) and nobelium (No); the en.microsoft
            // list additionally contains EVERY single letter, i.e. B, C, N, O, S, P, K, V, W, Y, F, H, U, I.
            // Under an English analyzer those symbols are stripped from the inverted index outright, so BM25
            // could never match a conclusion about arsenic from a query that says "As". An element-specific,
            // silent retrieval hole in an element-selection system.
            // The default analyzer does NOT strip them: Azure's `standard` analyzer chains the standard
            // tokenizer + lowercase + a stop filter whose stopword list "default is an empty list"
            // (learn.microsoft.com/azure/search/index-add-custom-analyzers, built-in analyzer table — contrast
            // the `stop` analyzer's "default is a predefined list for English"), and Microsoft's own worked
            // inverted-index example for a default-analyzer field visibly retains the/and/of/on/to/with
            // (learn.microsoft.com/azure/search/search-lucene-query-architecture). Language analyzers are
            // documented as *adding* stopword removal on top of Standard. Both sides lowercase, so "As" → `as`
            // on index and on query, and matches. Verified against the docs, not measured against a live index.
            new SearchableField("content"),

            // Scope + metadata. Filterable siblings of facts that also live inside `content` (the reader only
            // ever selects id/content); they exist for future filtered queries, and cost nothing.
            new SimpleField("kind", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("element", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("form", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("material", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("application", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("market", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("substance", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("confidence", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
            new SimpleField("createdAt", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },

            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true, VectorSearchDimensions = VectorDims, VectorSearchProfileName = VectorProfile
            }
        };

        return new SearchIndex(name, fields)
        {
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, VectorAlgo) },
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgo) }
            }
        };
    }

    public Task EnsureIndexAsync(CancellationToken ct = default) =>
        _indexClient.CreateOrUpdateIndexAsync(BuildIndex(_indexName), cancellationToken: ct);

    // AI Search caps a request at ~16 MB; a 3072-dim vector is ~12 KB, so a large push must be chunked
    // or the whole upload fails with HTTP 413 (learned on real data in the regulatory ingest).
    private const int PushBatch = 100;

    public async Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;
        var search = _indexClient.GetSearchClient(_indexName);
        for (var i = 0; i < chunks.Count; i += PushBatch)
        {
            var slice = chunks.Skip(i).Take(PushBatch).ToList();
            var response = await search.MergeOrUploadDocumentsAsync(slice, cancellationToken: ct);
            ThrowOnRejected(response.Value.Results);
        }
    }

    /// A 200 from AI Search does NOT mean the documents landed. MergeOrUpload returns 200 (or 207) with a
    /// PER-DOCUMENT result list, and a rejected document — an illegal key, a malformed vector — shows up
    /// only in there. Every other PushAsync in this repo ignores it, which is exactly how the illegal
    /// pipe-delimited document key (see LearnedConclusionProjection.SearchKey) could have failed silently
    /// and PERMANENTLY: Azure rejects the doc, the index stays empty, and search_learned_conclusions answers
    /// "no matches — do not fabricate a prior finding" forever, indistinguishable from a knowledge layer that
    /// is genuinely empty. A conclusion the operator cannot retrieve is worse than an exception; the exception
    /// at least surfaces in the revision's Error field where a human sees it.
    ///
    /// Done by hand rather than with IndexDocumentsOptions.ThrowOnAnyError on purpose. That flag does throw,
    /// but its message is only "Failed to index document(s): {keys}" — it carries the failing KEYS and drops
    /// each document's ErrorMessage and Status, i.e. precisely the part that says *why* it was rejected. Here
    /// the message is the diagnostic, so we build it ourselves: key, status and Azure's own reason, per doc.
    private static void ThrowOnRejected(IReadOnlyList<Azure.Search.Documents.Models.IndexingResult> results)
    {
        var rejected = results.Where(r => !r.Succeeded).ToList();
        if (rejected.Count == 0) return;

        var sb = new StringBuilder()
            .Append("learned-conclusions push rejected ").Append(rejected.Count).Append(" of ")
            .Append(results.Count).Append(" document(s); the index does NOT contain them:");
        foreach (var r in rejected)
            sb.Append("\n  key '").Append(r.Key).Append("' (status ").Append(r.Status).Append("): ")
              .Append(string.IsNullOrWhiteSpace(r.ErrorMessage) ? "(no error message)" : r.ErrorMessage);

        throw new InvalidOperationException(sb.ToString());
    }
}
