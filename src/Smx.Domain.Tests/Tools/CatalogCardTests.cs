using Smx.Domain.Tools;

namespace Smx.Domain.Tests.Tools;

public class CatalogCardTests
{
    [Fact]
    public void CatalogCard_CarriesFormAndCas()
    {
        var card = new CatalogCard("Y", "Y(TMHD)3", "TMHD complex", "15632-39-0", "99.9%", "ProChem", "ref-catalog/product|Y|Y(TMHD)3|ProChem");
        Assert.Equal("Y", card.Element);
        Assert.Equal("15632-39-0", card.Cas);
        Assert.Equal("ProChem", card.Supplier);
    }

    /// The Cost stage audits supplier figures, so the card must carry the free-text price/pack off ref-catalog.
    /// Asserting the values (not just that it compiles) is what fails if Price/Pack were dropped from the record.
    [Fact]
    public void CatalogCard_CarriesPriceAndPack_whenGiven()
    {
        var card = new CatalogCard("Sc", "Sc(TMHD)3", "TMHD complex", "15492-49-6", "99%", "J&K Scientific (STREM)",
            "ref-catalog/product|Sc|...", "$115.00", "500 mg");
        Assert.Equal("$115.00", card.Price);
        Assert.Equal("500 mg", card.Pack);
    }

    /// Most callers construct the 7-arg card and never touch price/pack — those must stay null, not empty or
    /// defaulted, so the Cost stage can tell "no price on this listing" from a real one.
    [Fact]
    public void CatalogCard_PriceAndPack_defaultToNull_whenOmitted()
    {
        var card = new CatalogCard("Y", "Y(TMHD)3", "TMHD complex", "15632-39-0", "99.9%", "ProChem", "ref-catalog/x");
        Assert.Null(card.Price);
        Assert.Null(card.Pack);
    }
}
