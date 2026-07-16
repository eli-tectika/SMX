using Microsoft.Azure.Cosmos;
using Smx.Domain;

namespace Smx.Infrastructure;

/// ISdsCorpusReader over the SDS library subsystem's `sds-registry` container (PK /cas; camelCase
/// documents written by the regsync Functions app). Read-only: this side NEVER writes the corpus.
/// Only current sheets participate — indexed, and not superseded by a newer revision.
public sealed class CosmosSdsCorpusReader(Container sdsRegistry) : ISdsCorpusReader
{
    public async Task<IReadOnlyList<SdsCorpusSheet>> QuerySheetsAsync(string? search, CancellationToken ct = default)
    {
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? Current
            : Current + " AND (CONTAINS(c.cas, @s, true) OR CONTAINS(c.supplier, @s, true))")
            .WithParameter("@s", search ?? "");
        return await RunAsync(q, ct);
    }

    public async Task<SdsCorpusSheet?> GetLatestForCasAsync(string cas, CancellationToken ct = default)
    {
        // Single-partition query (/cas); latest by revision date then ingestion time.
        var q = new QueryDefinition(Current + " AND c.cas = @cas").WithParameter("@cas", cas);
        var sheets = await RunAsync(q, ct);
        return sheets
            .OrderByDescending(s => s.RevisionDate, StringComparer.Ordinal)
            .ThenByDescending(s => s.IngestedUtc, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private const string Current =
        "SELECT c.cas, c.supplier, c.productName, c.revisionDate, c.ingestedUtc FROM c" +
        " WHERE c.indexed = true AND (NOT IS_DEFINED(c.supersededBy) OR IS_NULL(c.supersededBy))";

    private async Task<IReadOnlyList<SdsCorpusSheet>> RunAsync(QueryDefinition q, CancellationToken ct)
    {
        var results = new List<SdsCorpusSheet>();
        using var it = sdsRegistry.GetItemQueryIterator<SdsCorpusSheet>(q);
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
