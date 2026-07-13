using System.Text.Json;
using Azure.Search.Documents.Indexes.Models;
using Smx.Domain;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

/// The writer (LearnedConclusionChunk's [JsonPropertyName]s) and the index schema
/// (LearnedConclusionsIndex.BuildIndex) are two halves of one contract that nothing else enforces.
/// Drift between them fails SILENTLY — Azure drops an unknown field on push, and a field nobody
/// writes just stays empty — so the only place it can be caught is here.
public class LearnedConclusionsIndexSchemaTests
{
    private const string IndexName = "learned-conclusions";

    /// A chunk with every property non-null, so each one is guaranteed to emit its JSON key.
    private static LearnedConclusionChunk SampleChunk() => new(
        Id: "material-proj-1-abc", Content: "…", ContentVector: [0.1f, 0.2f],
        Kind: "material", Element: "As", Form: "oxide", Material: "PET",
        Application: "bottle", Market: "EU", Substance: "arsenic trioxide",
        Confidence: 0.9, CreatedAt: "2026-07-13T00:00:00Z");

    /// The wire names the push actually sends. Smx.Orchestrator builds bare SearchClients, so the
    /// serializer is the default one — exactly what is reconstructed here.
    private static HashSet<string> ChunkJsonKeys()
    {
        var json = JsonSerializer.Serialize(SampleChunk(), new JsonSerializerOptions());
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_field_the_chunk_emits_exists_in_the_index_schema()
    {
        var fields = LearnedConclusionsIndex.BuildIndex(IndexName).Fields
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        // Ordinal, not case-insensitive: AI Search field names are case-sensitive, so a "Content"
        // field would not be fed by a "content" property.
        Assert.Empty(ChunkJsonKeys().Except(fields, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_field_in_the_index_schema_is_emitted_by_the_chunk()
    {
        // The other direction: a schema field no chunk property feeds is a field that is always empty —
        // a filter or sort that silently matches nothing.
        var fields = LearnedConclusionsIndex.BuildIndex(IndexName).Fields.Select(f => f.Name);

        Assert.Empty(fields.Except(ChunkJsonKeys(), StringComparer.Ordinal));
    }

    [Fact]
    public void Content_is_searchable_and_id_is_the_key()
    {
        var fields = LearnedConclusionsIndex.BuildIndex(IndexName).Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        // LearnedConclusionsSearchTool reads `id` and `content` off a hit and nothing else. A non-searchable
        // `content` retrieves nothing on the BM25 half of the hybrid query — reported to the agent as
        // "no prior conclusions", not as a failure.
        Assert.True(fields["content"].IsSearchable);
        Assert.True(fields["id"].IsKey);
    }

    [Fact]
    public void Content_uses_the_default_analyzer_so_element_symbols_survive_indexing()
    {
        var index = LearnedConclusionsIndex.BuildIndex(IndexName);
        var content = index.Fields.Single(f => f.Name == "content");

        // Null AnalyzerName == Azure's default (standard.lucene), whose stopword list is empty. The English
        // analyzers would strip as/at/be/in/no — i.e. As, At, Be, In, No — and en.microsoft strips every
        // single-letter token, taking B, C, N, O, S, P, K, V, W, Y, F, H, U and I with it. In a taggant
        // system the element symbol is the single most important retrieval term.
        Assert.Null(content.AnalyzerName);
        Assert.Null(content.IndexAnalyzerName);
        Assert.Null(content.SearchAnalyzerName);
        Assert.Empty(index.Analyzers);
    }

    [Fact]
    public void Vector_field_is_3072_dims_and_binds_to_a_declared_hnsw_profile()
    {
        var index = LearnedConclusionsIndex.BuildIndex(IndexName);
        var vector = index.Fields.Single(f => f.Name == "contentVector");

        Assert.True(vector.IsSearchable);
        Assert.Equal(SearchFieldDataType.Collection(SearchFieldDataType.Single), vector.Type);
        // 3072 = text-embedding-3-large. A mismatch with BackendOptions.EmbeddingDeployment's model is
        // rejected by Azure at push time, per document.
        Assert.Equal(3072, vector.VectorSearchDimensions);

        // The profile the field names must actually be declared, and must point at a declared algorithm —
        // a dangling name is only caught when CreateOrUpdateIndex is called against a live service.
        var profile = Assert.Single(index.VectorSearch.Profiles, p => p.Name == vector.VectorSearchProfileName);
        Assert.Contains(index.VectorSearch.Algorithms, a => a.Name == profile.AlgorithmConfigurationName);
    }
}
