using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// ISdsDocumentSource over the SDS library subsystem's two containers: `sds-registry` (PK /cas,
/// written as RegistryPointer) and `sds-master-list` (PK /element, written as MasterListEntry).
/// Read-only — this side never writes the corpus.
///
/// camelCase projections throughout. These documents are written by the regsync Functions app with
/// CosmosPropertyNamingPolicy.CamelCase (Smx.Functions/Program.cs:56), and a PascalCase SELECT
/// returns nulls SILENTLY rather than failing — the failure mode that has bitten this estate before.
///
/// Unlike CosmosSdsCorpusReader over the same registry container, nothing here filters to current
/// sheets: the viewer must show a superseded sheet as history and a not-yet-indexed one as a fresh
/// upload. See ISdsDocumentSource's own comment for why that is two ports and not one.
public sealed class CosmosSdsDocumentSource(Container sdsRegistry, Container sdsMasterList) : ISdsDocumentSource
{
    // RegistryPointer also carries indexDocIds; the viewer has no use for the chunk key list, and
    // leaving it out of the projection keeps it off the wire.
    private const string SheetSelect =
        "SELECT c.id, c.cas, c.supplier, c.productName, c.revisionDate, c.region, c.language," +
        " c.sourceUrl, c.blobPath, c.indexed, c.ingestedUtc, c.supersededBy, c.masterListId FROM c";

    // MasterListEntry also carries substrateClass, addedBy and addedUtc — none of which the gap row
    // reports. Status, attemptCount, lastAttemptUtc and nextAttemptUtc are what
    // SdsDocumentProvider.Explain reads. nextAttemptUtc is absent on rows written before the
    // 2026-07-29 backoff, and Cosmos projects a missing property as null — which is exactly the
    // "no scheduled retry" the surface is built to say.
    private const string MasterSelect =
        "SELECT c.id, c.element, c.form, c.cas, c.status, c.lastAttemptUtc, c.attemptCount," +
        " c.nextAttemptUtc FROM c";

    public Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default)
        => QueryAsync<SdsSheetRow>(sdsRegistry, new QueryDefinition(SheetSelect), ct);

    /// The registry id is `cas|supplier|revisionDate` (DedupKey.ForRegistry) and the partition key is
    /// the CAS, so this is a point read.
    public Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default)
        => PointReadAsync<SdsSheetRow>(sdsRegistry, registryId, cas, ct);

    public Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default)
        => QueryAsync<SdsMasterRow>(sdsMasterList, new QueryDefinition(MasterSelect), ct);

    /// The master-list id is `{element}_{form}` (DedupKey.ForMasterList), partitioned by element.
    public Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default)
        => PointReadAsync<SdsMasterRow>(sdsMasterList, masterId, element, ct);

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(Container container, QueryDefinition q, CancellationToken ct)
    {
        var rows = new List<T>();
        using var it = container.GetItemQueryIterator<T>(q);
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    /// A point read returns the whole item rather than a projection; the extra fields simply have no
    /// property to bind to. Only 404 folds to null — a 403 (data-plane role not yet propagated) is
    /// left to throw, because "you may not read this" must not reach the operator as "not there".
    private static async Task<T?> PointReadAsync<T>(Container container, string id, string partitionKey, CancellationToken ct)
        where T : class
    {
        try
        {
            var resp = await container.ReadItemAsync<T>(id, new PartitionKey(partitionKey), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
}
