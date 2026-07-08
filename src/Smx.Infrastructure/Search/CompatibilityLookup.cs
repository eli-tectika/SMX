using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-compatibility container seeded by the reference-data subsystem (PK /element).
public sealed class CosmosCompatibilityLookup(Container container) : ICompatibilityLookup
{
    // Matches the reference-data seed shape (compatibility-rules.json → ref-compatibility, PK /element):
    // { id, element, docType, dimension, substrate, subject, verdict, reason, refIds[] }.
    private sealed record Row(string Id, string Element, string Substrate, string Verdict, string? Reason, List<string>? RefIds);

    public async Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.Substrate == substrate)
            .Take(1).ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var row in await it.ReadNextAsync(ct))
                return new CompatibilityCard(row.Element, row.Substrate, row.Verdict, row.Reason,
                    row.RefIds is { Count: > 0 } ? string.Join(",", row.RefIds) : row.Id);
        return null;
    }
}
