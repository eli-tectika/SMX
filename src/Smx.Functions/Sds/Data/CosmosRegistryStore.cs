using Microsoft.Azure.Cosmos;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class CosmosRegistryStore : IRegistryStore
{
    private readonly Container _c;
    public CosmosRegistryStore(Container container) => _c = container;

    public Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct)
        => FirstAsync("SELECT * FROM c WHERE c.cas = @v AND (NOT IS_DEFINED(c.supersededBy) OR c.supersededBy = null)", "@v", cas, ct);

    public Task<RegistryPointer?> GetByProductNameAsync(string name, CancellationToken ct)
        => FirstAsync("SELECT * FROM c WHERE c.productName = @v AND (NOT IS_DEFINED(c.supersededBy) OR c.supersededBy = null)", "@v", name, ct);

    public Task UpsertAsync(RegistryPointer p, CancellationToken ct)
        => _c.UpsertItemAsync(p, new PartitionKey(p.Cas), cancellationToken: ct);

    private async Task<RegistryPointer?> FirstAsync(string sql, string p, string v, CancellationToken ct)
    {
        var q = new QueryDefinition(sql).WithParameter(p, v);
        using var it = _c.GetItemQueryIterator<RegistryPointer>(q);
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync(ct)) return r;
        return null;
    }
}
