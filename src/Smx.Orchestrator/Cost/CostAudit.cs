using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Cost;

/// The Cost stage is DETERMINISTIC — no agent (spec §3.4). It audits the substances that will actually be
/// ORDERED (the ones named in the finalized codes) against the ref-catalog listings, and for each produces the
/// suppliers, the cheapest PARSEABLE price with the listing it came from, and the supply risks. It never
/// invents a number: an unparseable, non-USD, or absent price becomes a stated "quote required", never an
/// average, a guessed currency, or an FX-converted figure — because procurement cuts a purchase order against
/// whatever number sits here, and a fabricated one is the harm.
public static class CostAudit
{
    /// <param name="substances">The (CAS, element) pairs drawn from the finalized codes — the substances that
    /// will actually be ordered. The element selects the single ref-catalog partition to read; the CAS is the
    /// exact identifier the returned cards are filtered by.</param>
    /// <param name="generatedAt">Stamped onto every citation's RetrievedAt and onto the doc.</param>
    public static async Task<CostDoc> RunAsync(
        ICatalogLookup catalog,
        IReadOnlyList<(string Cas, string Element)> substances,
        string projectId = "",
        string generatedAt = "",
        CancellationToken ct = default)
    {
        var audits = new List<SupplierAudit>();

        foreach (var (cas, element) in substances)
        {
            var cards = await catalog.LookupAsync(element, ct);

            // A CAS is an exact identifier, so the match is case-sensitive (Ordinal): "1314-36-9" is one
            // substance and only one. A loose match here would fold two distinct compounds into one audited row.
            var matching = cards.Where(c => string.Equals(c.Cas, cas, StringComparison.Ordinal)).ToList();

            var suppliers = matching
                .Select(c => c.Supplier)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var risks = new List<string>();
            if (matching.Count == 0)
                // Not in the catalog at all. This is a FINDING, not an omission: dropping the row would let the
                // substance reach the VP looking cleanly costed when in truth nobody off-the-shelf lists it.
                risks.Add("not-off-the-shelf");
            else if (suppliers.Count == 1)
                risks.Add("single-source");

            // BestQuote = the CHEAPEST parseable $/g among the matching cards. Every quote PriceParse returns is
            // already USD (it refuses every other currency rather than convert), so the $/g figures are directly
            // comparable and "<" is a valid ordering. Unparseable cards are simply skipped — never averaged in,
            // never used as a fallback.
            PriceQuote? best = null;
            foreach (var card in matching)
            {
                var (quote, _) = PriceParse.Parse(card.Price, card.Pack);
                if (quote is null) continue;
                if (best is not null && quote.UsdPerGram >= best.UsdPerGram) continue;

                best = new PriceQuote(
                    quote.UsdPerGram, quote.Currency, card.Supplier, card.Pack ?? "",
                    new Citation("ref-catalog", CatalogReference(card.RefId), generatedAt));
            }

            var priceNote = best is null ? "no price on file — quote required" : "";
            audits.Add(new SupplierAudit(cas, element, suppliers, best, priceNote, risks));
        }

        return new CostDoc
        {
            Id = RecordIds.Cost(projectId),
            ProjectId = projectId,
            Substances = audits,
            GeneratedAt = generatedAt,
        };
    }

    /// The citation must trace to the ref-catalog listing. The infrastructure lookup already stamps RefId as
    /// "ref-catalog/{id}", but a test (or a future caller) may hand us a bare id — so prepend the prefix only
    /// when it is not already there, rather than doubling it.
    private static string CatalogReference(string refId) =>
        refId.StartsWith("ref-catalog/", StringComparison.Ordinal) ? refId : $"ref-catalog/{refId}";
}
