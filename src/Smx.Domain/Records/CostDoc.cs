namespace Smx.Domain.Records;

/// A price, and the listing it came from. Every figure in Cost carries its citation, because procurement
/// acts on these numbers and must be able to check them.
public sealed record PriceQuote(double UsdPerGram, string Currency, string Supplier, string Pack, Citation Citation);

/// The audit for one substance. `BestQuote` is null when nothing parseable was on file — and `PriceNote`
/// says so in words. Nothing is ever interpolated, averaged, or currency-converted into existence: price is
/// free text on a minority of catalog products, and a Cost stage that invented one would be fabricating the
/// single number procurement acts on.
public sealed record SupplierAudit(
    string Cas, string Element,
    IReadOnlyList<string> Suppliers,
    PriceQuote? BestQuote,
    string PriceNote,
    IReadOnlyList<string> Risks);      // "single-source" | "not-off-the-shelf"

public sealed class CostDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Cost;
    public List<SupplierAudit> Substances { get; set; } = [];
    public required string GeneratedAt { get; set; }
}
