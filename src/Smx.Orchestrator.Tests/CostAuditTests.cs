using Smx.Domain.Tools;
using Smx.Orchestrator.Cost;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The Cost stage is deterministic and its one job is to never invent a number: it quotes the cheapest
/// PARSEABLE price from the catalog with the listing it came from, says so in words when nothing is parseable,
/// and flags supply risks (single-source, not-off-the-shelf) rather than dropping a row.
public class CostAuditTests
{
    // A card with a price/pack. RefId is bare here (no "ref-catalog/" prefix) on purpose, so the citation test
    // proves CostAudit builds the prefix rather than passing whatever RefId happens to carry.
    private static CatalogCard Card(string cas, string element, string supplier, string refId,
        string? price = null, string? pack = null) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    [Fact]
    public async Task Audit_QuotesThePriceFromTheCatalog_AndCitesTheListing()
    {
        // one supplier, a parseable price → best quote with a ref-catalog citation. $66.00 / 25 g = 2.64 $/g.
        var catalog = new FakeCatalogLookup().Returns("Y",
            Card("1314-36-9", "Y", "Acme Chemicals", "cat-y-1", "$66.00", "25 g"));

        var audit = await CostAudit.RunAsync(catalog, [("1314-36-9", "Y")], ct: default);

        var line = Assert.Single(audit.Substances);
        Assert.Equal(2.64, line.BestQuote!.UsdPerGram, 2);
        Assert.Equal("USD", line.BestQuote.Currency);
        Assert.Equal("Acme Chemicals", line.BestQuote.Supplier);
        Assert.StartsWith("ref-catalog/", line.BestQuote.Citation.Reference);
        Assert.Equal("ref-catalog", line.BestQuote.Citation.Source);
        Assert.Contains("cat-y-1", line.BestQuote.Citation.Reference);   // it cites THIS listing, not a constant
    }

    [Fact]
    public async Task Audit_OnASubstanceWithNoParseablePrice_SaysSo_AndQuotesNothing()
    {
        // catalog card carries "Quote" (free text, unparseable) → BestQuote null, PriceNote says quote required.
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1", "Quote", "500 mg"));

        var audit = await CostAudit.RunAsync(catalog, [("cas-x", "Zr")], ct: default);

        var line = Assert.Single(audit.Substances);
        Assert.Null(line.BestQuote);
        Assert.Contains("quote required", line.PriceNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_FlagsASingleSourceSubstance_AsASupplyRisk()
    {
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1", "$66.00", "25 g"));

        var audit = await CostAudit.RunAsync(catalog, [("cas-x", "Zr")], ct: default);

        Assert.Contains("single-source", Assert.Single(audit.Substances).Risks);
    }

    [Fact]
    public async Task Audit_DoesNotFlagSingleSource_WhenTwoSuppliersListIt()
    {
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Acme Chemicals", "cat-zr-1", "$66.00", "25 g"),
            Card("cas-x", "Zr", "Beta Reagents", "cat-zr-2", "$66.00", "25 g"));

        var audit = await CostAudit.RunAsync(catalog, [("cas-x", "Zr")], ct: default);

        Assert.Empty(Assert.Single(audit.Substances).Risks);
    }

    [Fact]
    public async Task Audit_OnASubstanceMissingFromTheCatalogEntirely_IsNotSilent()
    {
        var catalog = new FakeCatalogLookup();   // nothing registered for "Zz"

        var audit = await CostAudit.RunAsync(catalog, [("cas-nowhere", "Zz")], ct: default);

        var line = Assert.Single(audit.Substances);
        Assert.Empty(line.Suppliers);
        Assert.Null(line.BestQuote);
        Assert.Contains("not-off-the-shelf", line.Risks);
    }

    [Fact]
    public async Task Audit_QuotesTheCheapestOfSeveralParseablePrices_AndCitesThatListing()
    {
        // Two priced cards for the same substance: $50/25 g = 2.00 $/g (cheap) vs $150/25 g = 6.00 $/g (dear).
        // The audit must quote 2.00 and cite the CHEAP card's listing — not the first, not the dearest.
        var catalog = new FakeCatalogLookup().Returns("Zr",
            Card("cas-x", "Zr", "Dear Supplier", "cat-dear", "$150.00", "25 g"),
            Card("cas-x", "Zr", "Cheap Supplier", "cat-cheap", "$50.00", "25 g"));

        var audit = await CostAudit.RunAsync(catalog, [("cas-x", "Zr")], ct: default);

        var line = Assert.Single(audit.Substances);
        Assert.Equal(2.00, line.BestQuote!.UsdPerGram, 2);
        Assert.Equal("Cheap Supplier", line.BestQuote.Supplier);
        Assert.Contains("cat-cheap", line.BestQuote.Citation.Reference);
        Assert.DoesNotContain("cat-dear", line.BestQuote.Citation.Reference);
    }
}
