using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-compatibility container seeded by the reference-data subsystem (PK /element).
public sealed class CosmosCompatibilityLookup(Container container) : ICompatibilityLookup
{
    private sealed record Row(string Id, string Element, string Substrate, string Verdict, string? Notes, string? RefId);

    public async Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.Substrate == substrate)
            .Take(1).ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var row in await it.ReadNextAsync(ct))
                return new CompatibilityCard(row.Element, row.Substrate, row.Verdict, row.Notes, row.RefId ?? row.Id);
        return null;
    }
}
