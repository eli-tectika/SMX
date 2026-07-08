using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// The Gold layer: a push-based, private AI Search index for the regulatory corpus. Mirrors
// Sds/Ingestion/SdsSearchClient — the index is created in code (data-plane; the workload has Search Index
// Data Contributor) since AI Search indexes have no ARM/Bicep resource type. Separate index from sds-index.
public sealed class RegSearchClient : IRegSearchClient
{
    private const int VectorDims = 3072; // text-embedding-3-large
    private const string VectorProfile = "reg-hnsw";
    private const string VectorAlgo = "reg-hnsw-config";
    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;

    public RegSearchClient(SearchIndexClient indexClient, string indexName)
    { _indexClient = indexClient; _indexName = indexName; }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SearchableField("content"),
            new SimpleField("regulation", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("authority", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("sourceId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("entryId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("docId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("sourceUrl", SearchFieldDataType.String),
            new SimpleField("officialDate", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
            new SimpleField("syncDate", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
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

    public async Task PushAsync(IReadOnlyList<GoldChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        var search = _indexClient.GetSearchClient(_indexName);
        await search.MergeOrUploadDocumentsAsync(chunks, cancellationToken: ct);
    }
}
