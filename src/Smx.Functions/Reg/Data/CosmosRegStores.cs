using Microsoft.Azure.Cosmos;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Data;

// Thin Cosmos wrappers, mirroring Sds/Data/CosmosRegistryStore. Containers are provisioned in Bicep
// (the workload identity has Cosmos data-plane rights only and cannot create them — SDS doc D3), so these
// classes take an existing Container and never create one. CosmosClient is registered CamelCase, so record
// property names map to camelCase item/partition-key fields (sourceId, docId, syncRunId, status).

public sealed class CosmosRegStateStore : IRegStateStore
{
    private readonly Container _c;
    public CosmosRegStateStore(Container container) => _c = container;

    public async Task<RegDocState?> GetAsync(string docId, string sourceId, CancellationToken ct)
    {
        try
        {
            var resp = await _c.ReadItemAsync<RegDocState>(docId, new PartitionKey(sourceId), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task UpsertAsync(RegDocState state, CancellationToken ct)
        => _c.UpsertItemAsync(state, new PartitionKey(state.SourceId), cancellationToken: ct);
}

public sealed class CosmosRegSilverStore : IRegSilverStore
{
    private readonly Container _c;
    public CosmosRegSilverStore(Container container) => _c = container;

    public async Task UpsertStagedAsync(IReadOnlyList<SilverChunk> chunks, CancellationToken ct)
    {
        foreach (var chunk in chunks)
            await _c.UpsertItemAsync(chunk, new PartitionKey(chunk.DocId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<SilverChunk>> GetStagedAsync(string runId, CancellationToken ct)
        => await QueryAsync(
            "SELECT * FROM c WHERE c.syncRunId = @r AND c.status = @s", ("@r", runId), ("@s", "staged"), ct);

    public async Task PromoteStagedToLiveAsync(string runId, IReadOnlyList<string> changedDocIds, CancellationToken ct)
    {
        // Supersede the prior live chunks for each changed doc (single-partition per doc)...
        foreach (var docId in changedDocIds)
        {
            var prior = await QueryAsync(
                "SELECT * FROM c WHERE c.docId = @d AND c.status = @s", ("@d", docId), ("@s", "live"), ct);
            foreach (var chunk in prior)
                await _c.UpsertItemAsync(chunk with { Status = "superseded" }, new PartitionKey(chunk.DocId), cancellationToken: ct);
        }
        // ...then promote this run's staged chunks to live.
        foreach (var chunk in await GetStagedAsync(runId, ct))
            await _c.UpsertItemAsync(chunk with { Status = "live" }, new PartitionKey(chunk.DocId), cancellationToken: ct);
    }

    public async Task DiscardStagedAsync(string runId, CancellationToken ct)
    {
        foreach (var chunk in await GetStagedAsync(runId, ct))
            await _c.UpsertItemAsync(chunk with { Status = "superseded" }, new PartitionKey(chunk.DocId), cancellationToken: ct);
    }

    private async Task<IReadOnlyList<SilverChunk>> QueryAsync(string sql, (string, string) p1, (string, string) p2, CancellationToken ct)
    {
        var q = new QueryDefinition(sql).WithParameter(p1.Item1, p1.Item2).WithParameter(p2.Item1, p2.Item2);
        var results = new List<SilverChunk>();
        using var it = _c.GetItemQueryIterator<SilverChunk>(q);
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}

public sealed class CosmosRegReviewStore : IRegReviewStore
{
    private readonly Container _c;
    public CosmosRegReviewStore(Container container) => _c = container;

    public Task UpsertAsync(ReviewRecord record, CancellationToken ct)
        => _c.UpsertItemAsync(record, new PartitionKey(record.SyncRunId), cancellationToken: ct);

    public async Task<ReviewRecord?> GetAsync(string runId, CancellationToken ct)
    {
        try
        {
            var resp = await _c.ReadItemAsync<ReviewRecord>(runId, new PartitionKey(runId), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<IReadOnlyList<ReviewRecord>> GetByStatusAsync(string status, CancellationToken ct)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.status = @s").WithParameter("@s", status);
        var results = new List<ReviewRecord>();
        using var it = _c.GetItemQueryIterator<ReviewRecord>(q);
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}

public sealed class CosmosRegRunsStore : IRegRunsStore
{
    private readonly Container _c;
    public CosmosRegRunsStore(Container container) => _c = container;

    public Task UpsertAsync(SyncRun run, CancellationToken ct)
        => _c.UpsertItemAsync(run, new PartitionKey(run.SyncRunId), cancellationToken: ct);
}

public sealed class CosmosRegRegistryStore : IRegRegistryStore
{
    private readonly Container _c;
    public CosmosRegRegistryStore(Container container) => _c = container;

    public async Task<IReadOnlyList<RegSource>> GetEnabledAsync(CancellationToken ct)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.enabled = true");
        var results = new List<RegSource>();
        using var it = _c.GetItemQueryIterator<RegSource>(q);
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }

    public Task UpsertAsync(RegSource source, CancellationToken ct)
        => _c.UpsertItemAsync(source, new PartitionKey(source.SourceId), cancellationToken: ct);
}
