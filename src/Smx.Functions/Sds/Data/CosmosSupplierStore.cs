using Microsoft.Azure.Cosmos;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

/// `sds-suppliers`, partitioned by `/domain`. Assumes the camelCase Cosmos serializer configured in
/// Program.cs, exactly as CosmosMasterListStore does — `AllowlistEntry.Id` and `.Domain` must land as
/// `id` and `domain` or the point read below finds nothing.
public sealed class CosmosSupplierStore : ISupplierStore
{
    private readonly Container _c;
    public CosmosSupplierStore(Container container) => _c = container;

    public async Task<AllowlistEntry?> GetAsync(string domain, CancellationToken ct)
    {
        var key = domain.ToLowerInvariant();
        try { return await _c.ReadItemAsync<AllowlistEntry>(key, new PartitionKey(key), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    // The stored `domain` is written lowercased so it always equals the derived `id` — Cosmos rejects an
    // upsert whose partition-key argument disagrees with the document's partition-key field.
    public Task UpsertAsync(AllowlistEntry entry, CancellationToken ct)
        => _c.UpsertItemAsync(entry with { Domain = entry.Id }, new PartitionKey(entry.Id), cancellationToken: ct);

    public async Task<IReadOnlyList<AllowlistEntry>> ListAllAsync(CancellationToken ct)
    {
        var results = new List<AllowlistEntry>();
        using var it = _c.GetItemQueryIterator<AllowlistEntry>("SELECT * FROM c");
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
