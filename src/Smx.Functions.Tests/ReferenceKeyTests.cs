using Smx.Functions.Reference.Domain;
using Xunit;

public class ReferenceKeyTests
{
    [Fact]
    public void Slug_is_lowercase_hyphenated_and_trimmed()
    {
        Assert.Equal("y-tmhd-3", ReferenceKey.Slug("  Y(TMHD)3 "));
        Assert.Equal("sigma-aldrich-merck", ReferenceKey.Slug("Sigma-Aldrich / Merck"));
    }

    [Fact]
    public void DocId_composes_docType_element_and_discriminator()
    {
        Assert.Equal("rule|Zr|gold-solubility",
            ReferenceKey.DocId("rule", "Zr", "Gold solubility"));
    }
}
