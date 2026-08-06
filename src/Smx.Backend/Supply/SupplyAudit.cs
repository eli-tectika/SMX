using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Backend.Supply;

/// The supply audit — DETERMINISTIC, no agent. It reads the substances that will actually be ORDERED (the
/// ones named in the finalized codes) against the ref-catalog listings and reports, per substance, who sells
/// it and what is risky about that.
///
/// It was <c>CostAudit</c>, and it computed a price. The customer has confirmed there are no price details
/// (redesign spec §6), so the price path is gone rather than kept and hidden: a nullable figure that is now
/// known to be always absent produces a column reading "—" forever and a parser nobody can exercise.
///
/// Nothing else was lost with it. Neither risk was ever derived from a price — "single-source" is a supplier
/// count and "not-off-the-shelf" is absence from the catalog — so the two findings procurement acts on are
/// exactly as strong as they were.
public static class SupplyAudit
{
    /// <param name="substances">The (CAS, element) pairs drawn from the finalized codes — the substances that
    /// will actually be ordered. The element selects the single ref-catalog partition to read; the CAS is the
    /// exact identifier the returned cards are filtered by.</param>
    public static async Task<List<SupplierAudit>> RunAsync(
        ICatalogLookup catalog,
        IReadOnlyList<(string Cas, string Element)> substances,
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
                // substance reach the VP looking cleanly sourced when in truth nobody off-the-shelf lists it.
                risks.Add("not-off-the-shelf");
            else if (suppliers.Count == 1)
                risks.Add("single-source");

            audits.Add(new SupplierAudit(cas, element, suppliers, risks));
        }

        return audits;
    }
}
