using Smx.Domain.Tools;
using Smx.Backend.Supply;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The supply audit — deterministic, no agent. It was CostAuditTests; the six cases here are the four that
/// survive plus a new one, and the two that went were about PRICES (parsing the cheapest quote, and saying
/// "quote required" when nothing parsed). The customer has confirmed there are no price details, so the
/// price path is deleted rather than kept and hidden (redesign spec §6).
///
/// Nothing about the RISKS moved: "single-source" was always a supplier count and "not-off-the-shelf" was
/// always absence from the catalog. Neither was ever derived from a price, which is why deleting the price
/// path costs these findings nothing — and that is worth a test of its own.
public class SupplyAuditTests
{
    private static CatalogCard Card(string cas, string element, string supplier, string refId,
        string? price = null, string? pack = null) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    [Fact]
    public async Task Audit_ListsEverySupplierOfTheSubstance()
    {
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1"),
            Card("cas-x", "Zr", "Beta Reagents", "cat-zr-2"));

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-x", "Zr")]);

        Assert.Equal(["Acme Chemicals", "Beta Reagents"], Assert.Single(audit).Suppliers);
    }

    [Fact]
    public async Task Audit_FlagsASingleSourceSubstance_AsASupplyRisk()
    {
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1", "$66.00", "25 g"));

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-x", "Zr")]);

        Assert.Contains("single-source", Assert.Single(audit).Risks);
    }

    [Fact]
    public async Task Audit_DoesNotFlagSingleSource_WhenTwoSuppliersListIt()
    {
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1", "$66.00", "25 g"),
            Card("cas-x", "Zr", "Beta Reagents", "cat-zr-2", "$66.00", "25 g"));

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-x", "Zr")]);

        Assert.Empty(Assert.Single(audit).Risks);
    }

    [Fact]
    public async Task Audit_OnASubstanceMissingFromTheCatalogEntirely_IsNotSilent()
    {
        var catalog = new FakeCatalogLookup();   // nothing registered for "Zz"

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-nowhere", "Zz")]);

        var line = Assert.Single(audit);
        Assert.Empty(line.Suppliers);
        Assert.Contains("not-off-the-shelf", line.Risks);
    }

    [Fact]
    public async Task Audit_RisksAreUnaffectedByTheAbsenceOfAPrice()
    {
        // The regression guard for deleting the price path. Both cards are priceless; the risk verdicts must
        // be exactly what they would have been with prices on them. If "not-off-the-shelf" or "single-source"
        // ever started keying off a price field, every substance would be flagged the moment prices vanished.
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1"),
            Card("cas-x", "Zr", "Beta Reagents", "cat-zr-2"));

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-x", "Zr")]);

        Assert.Empty(Assert.Single(audit).Risks);
    }

    [Fact]
    public async Task Audit_MatchesTheCasOrdinally_NeverFoldingTwoCompoundsIntoOneRow()
    {
        // A CAS is an exact identifier. A loose match would report one substance's suppliers under another's
        // row, and procurement would order against it.
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1"),
            Card("CAS-X", "Zr", "Wrong Supplier", "cat-zr-2"));

        var audit = await SupplyAudit.RunAsync(catalog, [("cas-x", "Zr")]);

        Assert.Equal(["Acme Chemicals"], Assert.Single(audit).Suppliers);
    }
}
