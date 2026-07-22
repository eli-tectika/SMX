using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IRegDocumentSource over `reg-registry` (curated official sources, PK /sourceId, written as
/// RegSource) and `reg-state` (per-document change-detection state, PK /sourceId, written as
/// RegDocState). Read-only — the monthly sync is the only writer.
///
/// camelCase projections, as everywhere against the regsync estate: a PascalCase SELECT returns
/// nulls silently rather than failing.
public sealed class CosmosRegDocumentSource(Container regRegistry, Container regState) : IRegDocumentSource
{
    // reg-state's `id` IS the docId — CosmosRegStateStore reads it as ReadItemAsync(docId, pk sourceId)
    // and there is no separate `docId` field on the item. Projecting c.docId would bind null onto every
    // row, and RegDocumentProvider would then build a bronze path with a hole in it.
    private const string DocSelect =
        "SELECT c.id AS docId, c.sourceId, c.sha256, c.officialDate, c.syncRunId, c.lastFetchTs FROM c";

    /// Every curated source, not just the enabled ones. `enabled` governs whether the monthly sync
    /// still fetches a source; documents already in bronze from a source since switched off are still
    /// there, still cited, and must still open — and membership here is exactly what tells a synced
    /// document (`regulatory/` layout) from a seed-imported one (`seed/`).
    public async Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT c.sourceId, c.regulation, c.authority, c.documents FROM c");
        var rows = new List<RegSourceRow>();
        using var it = regRegistry.GetItemQueryIterator<RegSourceRow>(q);
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    public async Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default)
    {
        var rows = new List<RegDocRow>();
        using var it = regState.GetItemQueryIterator<RegDocRow>(new QueryDefinition(DocSelect));
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    /// A query rather than a point read, because the alias is what supplies DocId: ReadItemAsync
    /// returns the raw item, whose docId field does not exist. Still single-partition — the caller
    /// knows the sourceId, so this costs one partition, not a fan-out.
    public async Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(DocSelect + " WHERE c.id = @docId").WithParameter("@docId", docId);
        using var it = regState.GetItemQueryIterator<RegDocRow>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(sourceId) });
        while (it.HasMoreResults)
            foreach (var row in await it.ReadNextAsync(ct)) return row;
        return null;
    }
}
