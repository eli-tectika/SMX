using Microsoft.Azure.Cosmos;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class CosmosMasterListStore : IMasterListStore
{
    private readonly Container _c;
    public CosmosMasterListStore(Container container) => _c = container;

    public async Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct)
    {
        try { return await _c.ReadItemAsync<MasterListEntry>(id, new PartitionKey(element), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task UpsertAsync(MasterListEntry entry, CancellationToken ct)
        => _c.UpsertItemAsync(entry, new PartitionKey(entry.Element), cancellationToken: ct);

    public async Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct)
    {
        var results = new List<MasterListEntry>();
        using var it = _c.GetItemQueryIterator<MasterListEntry>("SELECT * FROM c");
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
