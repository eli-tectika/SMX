using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Smx.SearchProxy;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class HostWiringTests
{
    private static IServiceProvider Build(params (string, string)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Item1, s => (string?)s.Item2))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        ProxyHost.ConfigureServices(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DryRun_BuildsWithNoKeyAndNoStorage()
    {
        var sp = Build(("PROXY_DRY_RUN", "true"));
        Assert.IsType<DryRunSearchProvider>(sp.GetRequiredService<ISearchProvider>());
        Assert.NotNull(sp.GetRequiredService<SearchPipeline>());
    }

    // The dry-run stores are not a shortcut around the pipeline: the same guard, cover batch and quota code
    // runs, against stores that never hit and never bind.
    [Fact]
    public void DryRun_UsesTheNullStores()
    {
        var sp = Build(("PROXY_DRY_RUN", "true"));
        Assert.IsType<NullSearchCache>(sp.GetRequiredService<ISearchCache>());
        Assert.IsType<NullQuotaStore>(sp.GetRequiredService<IQuotaStore>());
    }

    [Fact]
    public void Live_SelectsTheBraveProvider()
    {
        var sp = Build(
            ("PROXY_DRY_RUN", "false"),
            ("PROXY_SEARCH_API_KEY", "k"),
            ("AzureWebJobsStorage__accountName", "stfnspexample"));
        Assert.IsType<BraveSearchProvider>(sp.GetRequiredService<ISearchProvider>());
    }

    [Fact]
    public void Live_BuildsTheWholePipeline()
    {
        var sp = Build(
            ("PROXY_DRY_RUN", "false"),
            ("PROXY_SEARCH_API_KEY", "k"),
            ("AzureWebJobsStorage__accountName", "stfnspexample"));
        Assert.NotNull(sp.GetRequiredService<SearchPipeline>());
        Assert.IsType<BlobSearchCache>(sp.GetRequiredService<ISearchCache>());
        Assert.IsType<BlobQuotaStore>(sp.GetRequiredService<IQuotaStore>());
    }

    // ── FIX C: the cover count and the corpus must be cross-checked, or the anonymity set shrinks in silence ──

    // CoverBatch draws its decoys with Take(CoverCount - 1), and Take() under-fills without complaint. Raise
    // PROXY_COVER_COUNT above the thinnest family and every test stays green while the real query egresses in
    // a batch smaller than the operator configured. The one property this component exists to provide would
    // degrade with no signal at all. So it is a startup failure — at deploy time, in the open.
    [Fact]
    public void ACoverCountTheCorpusCannotFill_FailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            ("PROXY_DRY_RUN", "true"),
            ("PROXY_COVER_COUNT", "1000")));   // no family in the shipped corpus is near this

        Assert.Contains("PROXY_COVER_COUNT=1000", ex.Message);
        Assert.Contains("anonymity set", ex.Message);
    }

    [Fact]
    public void TheShippedCorpusFillsTheDefaultCoverCount()
    {
        // The default (4) against the real artifact, loaded exactly as production loads it.
        var sp = Build(("PROXY_DRY_RUN", "true"));
        Assert.NotNull(sp.GetRequiredService<CoverBatch>());
    }

    // The boundary, stated exactly: a family of N decoys can fill a batch of N, but not N + 1. It is N and not
    // N + 1 because CoverBatch drops a decoy that duplicates the real query before it takes its N - 1.
    [Theory]
    [InlineData(20, 20, false)]
    [InlineData(20, 21, true)]
    public void TheCheckIsExactAtTheBoundary(int perFamily, int coverCount, bool shouldThrow)
    {
        var corpus = CoverCorpus.FromJson(
            "{" + string.Join(",", SearchIntents.All.Select(i =>
                $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, perFamily).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");

        var boom = Record.Exception(() => ProxyHost.EnsureCorpusCanFillTheBatch(corpus, coverCount));

        if (shouldThrow) Assert.IsType<InvalidOperationException>(boom);
        else Assert.Null(boom);
    }
}
