using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Sourcing;
using Xunit;

public class SourceResolverTests
{
    private static readonly EgressFetch NoFetch =
        (_, _) => throw new InvalidOperationException("casTemplate must not fetch");

    [Fact]
    public async Task CasTemplate_substitutes_cas_into_url()
    {
        var entry = new AllowlistEntry("ChemBlink", "chemblink.com", 90, "casTemplate",
            "https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf", null, null);
        var strat = new CasTemplateStrategy();
        var got = await strat.ResolveAsync(entry, new SubstanceKey("Yb", "oxide", "1314-37-0"), NoFetch, default);
        Assert.Single(got);
        Assert.Equal("https://www.chemblink.com/MSDS/MSDSFiles/1314-37-0.pdf", got[0].Url.ToString());
        Assert.Equal("chemblink.com", got[0].Domain);
    }

    [Fact]
    public async Task ProductLookup_resolves_cas_to_brand_and_product_then_builds_sds_url()
    {
        var entry = new AllowlistEntry("Sigma-Aldrich", "sigmaaldrich.com", 10, "productLookup",
            "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}",
            "https://www.sigmaaldrich.com/US/en/search/{cas}",
            "/sds/(?<brand>[a-z]+)/(?<productNumber>[a-z0-9]+)");

        EgressFetch fetch = (url, _) =>
        {
            var html = "<a href=\"/US/en/sds/sigald/329460\">SDS</a>";
            return Task.FromResult<EgressResult?>(
                new EgressResult(System.Text.Encoding.UTF8.GetBytes(html), "text/html", url));
        };

        var got = await new ProductLookupStrategy().ResolveAsync(
            entry, new SubstanceKey("Na", "hydroxide", "1310-73-2"), fetch, default);

        Assert.Single(got);
        Assert.Equal("https://www.sigmaaldrich.com/US/en/sds/sigald/329460", got[0].Url.ToString());
    }

    [Fact]
    public async Task ProductLookup_returns_empty_when_search_yields_no_match()
    {
        var entry = new AllowlistEntry("Sigma-Aldrich", "sigmaaldrich.com", 10, "productLookup",
            "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}",
            "https://www.sigmaaldrich.com/US/en/search/{cas}",
            "/sds/(?<brand>[a-z]+)/(?<productNumber>[a-z0-9]+)");
        EgressFetch fetch = (url, _) =>
            Task.FromResult<EgressResult?>(new EgressResult(System.Text.Encoding.UTF8.GetBytes("no match"), "text/html", url));
        var got = await new ProductLookupStrategy().ResolveAsync(
            entry, new SubstanceKey("Na", "hydroxide", "1310-73-2"), fetch, default);
        Assert.Empty(got);
    }

    [Fact]
    public async Task Resolver_emits_candidates_in_priority_order()
    {
        var json = """
        [
          { "supplier":"ChemBlink","domain":"chemblink.com","priority":90,"strategy":"casTemplate",
            "sdsUrlTemplate":"https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf" },
          { "supplier":"Manu","domain":"manu.com","priority":10,"strategy":"casTemplate",
            "sdsUrlTemplate":"https://manu.com/{cas}.pdf" }
        ]
        """;
        var allow = AllowlistProvider.FromJson(json);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var got = await resolver.ResolveAsync(new SubstanceKey("Yb", "oxide", "1314-37-0"), NoFetch, default);
        Assert.Equal(new[] { "manu.com", "chemblink.com" }, got.Select(c => c.Domain).ToArray());
    }
}
