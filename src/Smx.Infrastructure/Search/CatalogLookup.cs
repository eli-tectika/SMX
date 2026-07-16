using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-catalog container seeded by the reference-data subsystem (PK /element, docType "product").
public sealed class CosmosCatalogLookup(Container container) : ICatalogLookup
{
    // internal (not private) so CosmosQueryTextTests can pin the SQL this row shape generates.
    // Price and Pack are the free-text supplier figures the Cost stage audits — the seed carries them as
    // camelCase `price`/`pack` (see Reference/Seed/catalog-products.json), which the serializer's member
    // naming reproduces. A PascalCase mismatch reads zero docs in Azure silently; CosmosQueryTextTests pins it.
    internal sealed record Row(string Id, string Element, string DocType, string? Molecule, string? Compound, string? Cas, string? Purity, string? Supplier, string? Price, string? Pack);

    public async Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        var results = new List<CatalogCard>();
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.DocType == "product")
            // Explicit projection so the wire names (incl. price/pack) land in the emitted SQL, where
            // CosmosQueryTextTests can pin them camelCase — the only way a fake-backed suite catches the
            // silent-in-Azure PascalCase trap. (Cosmos LINQ rejects positional-record construction, so this
            // projects an anonymous type; both the source access and the output alias come out camelCase, so
            // it round-trips under Json.Options' naming policy.)
            .Select(r => new { r.Id, r.Element, r.Molecule, r.Compound, r.Cas, r.Purity, r.Supplier, r.Price, r.Pack })
            .ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync(ct))
                results.Add(new CatalogCard(r.Element, r.Molecule ?? "", r.Compound ?? "", r.Cas ?? "",
                    r.Purity, r.Supplier ?? "", $"ref-catalog/{r.Id}", r.Price, r.Pack));
        return results;
    }
}
