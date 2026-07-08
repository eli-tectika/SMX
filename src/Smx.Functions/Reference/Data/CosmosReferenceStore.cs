// src/Smx.Functions/Reference/Data/CosmosReferenceStore.cs
using Microsoft.Azure.Cosmos;

namespace Smx.Functions.Reference.Data;

public sealed class CosmosReferenceStore : IReferenceStore
{
    private readonly CosmosClient _client;
    private readonly string _database;
    public CosmosReferenceStore(CosmosClient client, string database)
    { _client = client; _database = database; }

    public Task UpsertAsync(string container, object doc, string partitionValue, CancellationToken ct)
        => _client.GetContainer(_database, container)
                  .UpsertItemAsync(doc, new PartitionKey(partitionValue), cancellationToken: ct);
}
