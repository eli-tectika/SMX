// src/Smx.Functions/Reference/Ingestion/ReferenceSearchClient.cs
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Functions.Reference.Domain;

namespace Smx.Functions.Reference.Ingestion;

public sealed class ReferenceSearchClient : IReferenceSearchClient
{
    private const int VectorDims = 3072; // text-embedding-3-large
    private const string VectorProfile = "ref-hnsw";
    private const string VectorAlgo = "ref-hnsw-config";
    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;

    public ReferenceSearchClient(SearchIndexClient indexClient, string indexName)
    { _indexClient = indexClient; _indexName = indexName; }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SimpleField("element", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("substrate", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("dimension", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("verdict", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("refIds", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
            new SearchableField("sourceTitle"),
            new SimpleField("doi", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("url", SearchFieldDataType.String),
            new SimpleField("sheet", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("dataset", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("content"),
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true, VectorSearchDimensions = VectorDims, VectorSearchProfileName = VectorProfile
            }
        };
        var index = new SearchIndex(_indexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, VectorAlgo) },
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgo) }
            }
        };
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
    }

    public async Task PushAsync(IReadOnlyList<ReferenceChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        var search = _indexClient.GetSearchClient(_indexName);
        await search.MergeOrUploadDocumentsAsync(chunks, cancellationToken: ct);
    }
}
