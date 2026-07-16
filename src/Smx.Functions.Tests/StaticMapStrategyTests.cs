using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Sourcing;
using Xunit;

// The 2026-07-16 allowlist shakedown found exactly one live-fetchable SDS source (the
// fishersci.com msds endpoint) — and it needs a per-substance product number that no
// fetchable search can supply (the search is bot-walled). staticMap carries that mapping as
// curated, git-versioned allowlist data: CAS -> product number, resolved with zero extra
// egress. Same curation philosophy as the search-proxy cover corpus.
public class StaticMapStrategyTests
{
    private const string AllowJson = """
      [ { "supplier":"Alfa Aesar (Fisher)","domain":"fishersci.com","priority":10,"strategy":"staticMap",
          "sdsUrlTemplate":"https://www.fishersci.com/store/msds?countryCode=US&language=en&partNumber={productNumber}&vendorId=VN00024248",
          "casMap": { "1313-97-9": "AA11250", "12061-16-4": "AA11309" } } ]
    """;

    private static readonly EgressFetch NoFetch =
        (_, _) => throw new InvalidOperationException("staticMap must not egress");

    [Fact]
    public async Task Resolves_mapped_cas_to_the_templated_sds_url_without_egress()
    {
        var allow = AllowlistProvider.FromJson(AllowJson);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new StaticMapStrategy() });

        var candidates = await resolver.ResolveAsync(
            new SubstanceKey("Nd", "oxide", "1313-97-9"), NoFetch, default);

        var c = Assert.Single(candidates);
        Assert.Equal("fishersci.com", c.Domain);
        Assert.Equal(
            "https://www.fishersci.com/store/msds?countryCode=US&language=en&partNumber=AA11250&vendorId=VN00024248",
            c.Url.ToString());
    }

    [Fact]
    public async Task Unmapped_cas_yields_no_candidates()
    {
        var allow = AllowlistProvider.FromJson(AllowJson);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new StaticMapStrategy() });

        var candidates = await resolver.ResolveAsync(
            new SubstanceKey("Xx", "mystery", "0000-00-0"), NoFetch, default);

        Assert.Empty(candidates);
    }
}
