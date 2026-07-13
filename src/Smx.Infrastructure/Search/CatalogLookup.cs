using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-catalog container seeded by the reference-data subsystem (PK /element, docType "product").
public sealed class CosmosCatalogLookup(Container container) : ICatalogLookup
{
    // internal (not private) so CosmosQueryTextTests can pin the SQL this row shape generates.
    internal sealed record Row(string Id, string Element, string DocType, string? Molecule, string? Compound, string? Cas, string? Purity, string? Supplier);

    public async Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        var results = new List<CatalogCard>();
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.DocType == "product")
            .ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync(ct))
                results.Add(new CatalogCard(r.Element, r.Molecule ?? "", r.Compound ?? "", r.Cas ?? "",
                    r.Purity, r.Supplier ?? "", $"ref-catalog/{r.Id}"));
        return results;
    }
}
