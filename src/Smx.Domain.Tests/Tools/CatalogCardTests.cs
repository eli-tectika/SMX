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
}
