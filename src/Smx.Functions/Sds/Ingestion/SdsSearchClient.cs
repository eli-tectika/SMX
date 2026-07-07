using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class SdsSearchClient : ISdsSearchClient
{
    private const int VectorDims = 3072; // text-embedding-3-large
    private const string VectorProfile = "sds-hnsw";
    private const string VectorAlgo = "sds-hnsw-config";
    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;

    public SdsSearchClient(SearchIndexClient indexClient, string indexName)
    { _indexClient = indexClient; _indexName = indexName; }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SimpleField("cas", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("supplier", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("productName"),
            new SimpleField("revisionDate", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("region", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("language", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("ghsSection", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("content"),
            new SimpleField("blobPath", SearchFieldDataType.String),
            new SimpleField("masterListId", SearchFieldDataType.String) { IsFilterable = true },
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

    public async Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        var search = _indexClient.GetSearchClient(_indexName);
        await search.MergeOrUploadDocumentsAsync(chunks, cancellationToken: ct);
    }
}
