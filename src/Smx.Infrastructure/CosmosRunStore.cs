using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// The `runs` container, partitioned by /projectId.
public sealed class CosmosRunStore(Container container) : IRunStore
{
    public Task UpsertAsync(RunDoc run, CancellationToken ct) =>
        container.UpsertItemAsync(run, new PartitionKey(run.ProjectId), cancellationToken: ct);

    /// Oldest first, ordered on the STRING: RunDoc.StartedAt is always a fixed-width "O"-format
    /// DateTimeOffset with a fixed +00:00 offset, so lexicographic order IS chronological order
    /// (the same reasoning CosmosRecordStore.GetRevisionsAsync leans on for CreatedAt).
    ///
    /// LINQ, not a hand-written SQL string: the SDK's LINQ provider is wired (via
    /// SystemTextJsonCosmosSerializer.SerializeMemberName, see CosmosClientOptions in Program.cs) to emit
    /// the same camelCase wire names Json.Options writes, so `d.Stage` here already becomes
    /// `root["stage"]`. A raw `QueryDefinition("... r.stage ...")` string would bypass that translation
    /// entirely and reintroduce the exact silent-empty-result trap CosmosQueryTextTests exists to catch.
    public async Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct)
    {
        IQueryable<RunDoc> queryable = container.GetItemLinqQueryable<RunDoc>(
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) });
        if (stage is not null)
            queryable = queryable.Where(d => d.Stage == stage);

        var results = new List<RunDoc>();
        var query = queryable.OrderBy(d => d.StartedAt).ToFeedIterator();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }

    public async Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct)
    {
        try
        {
            return (await container.ReadItemAsync<RunDoc>(runId, new PartitionKey(projectId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
