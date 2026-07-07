using Smx.Functions.Sds.Domain;
using Xunit;

public class DedupKeyTests
{
    [Fact]
    public void MasterListId_slugs_element_and_form()
        => Assert.Equal("Yb_neodecanoate", DedupKey.ForMasterList("Yb", "Neodecanoate"));

    [Fact]
    public void MasterListId_slug_replaces_spaces_and_lowercases_form()
        => Assert.Equal("Ti_titanium-dioxide", DedupKey.ForMasterList("Ti", "Titanium Dioxide"));

    [Fact]
    public void RegistryId_is_cas_supplier_revision_normalized()
        => Assert.Equal("27253-31-2|strem|2024-03-01",
            DedupKey.ForRegistry(" 27253-31-2 ", "Strem", "2024-03-01"));

    [Fact]
    public void RegistryId_same_cas_different_supplier_or_revision_are_distinct()
    {
        var a = DedupKey.ForRegistry("1", "sigma", "2024-01-01");
        var b = DedupKey.ForRegistry("1", "sigma", "2024-06-01");
        var c = DedupKey.ForRegistry("1", "strem", "2024-01-01");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }
}
