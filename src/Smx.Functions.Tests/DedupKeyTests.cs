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

    // Regression guard for the live 2026-07-16 Search push rejection: the chunk document key was
    // '{registryId}#{i}' — '|', spaces, and '#' are all illegal in Azure AI Search keys
    // ("Keys can only contain letters, digits, underscore (_), dash (-), or equal sign (=)").
    [Fact]
    public void ChunkKey_uses_only_search_legal_characters()
    {
        var registryId = DedupKey.ForRegistry("1313-97-9", "Stanford Advanced Materials", "2022-11-02");
        var key = DedupKey.ForChunk(registryId, 0);
        Assert.Matches("^[A-Za-z0-9_=-]+$", key);
    }

    [Fact]
    public void ChunkKey_is_deterministic_and_distinct_per_registry_and_ordinal()
    {
        var a0 = DedupKey.ForChunk("1|a b|d", 0);
        var a1 = DedupKey.ForChunk("1|a b|d", 1);
        var b0 = DedupKey.ForChunk("1|a-b|d", 0);   // slug-collision shape must stay distinct
        Assert.Equal(a0, DedupKey.ForChunk("1|a b|d", 0));
        Assert.NotEqual(a0, a1);
        Assert.NotEqual(a0, b0);
    }
}
