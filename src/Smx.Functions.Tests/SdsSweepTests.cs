using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;
using Smx.Functions.Sds.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class SdsSweepTests
{
    private sealed class TextExtractor : IPdfTextExtractor
    { public string Extract(byte[] pdf) => System.Text.Encoding.UTF8.GetString(pdf); }

    [Fact]
    public async Task DryRun_sweep_fetches_via_dry_client_ingests_and_marks_fetched()
    {
        var mlStore = new InMemoryMasterListStore();
        var mlRepo = new MasterListRepo(mlStore);
        await mlRepo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var allow = AllowlistProvider.FromJson("""
          [ { "supplier":"ChemBlink","domain":"chemblink.com","priority":90,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf" } ]
        """);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });

        var cannedPdf = File.ReadAllBytes("Resources/sample_sds.txt");     // text-as-"pdf" for the TextExtractor
        var egress = DryRunEgressClient.Default(cannedPdf);

        var search = new FakeSearchClient(); var reg = new InMemoryRegistryStore();
        var domains = allow.Domains;
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new TextExtractor(),
            new GhsChunker(), new FakeEmbedder(), search, new RegistryRepo(reg), domains, new SdsOptions());

        var sweep = new SdsSweep(mlRepo, resolver, egress, pipe, new SdsOptions(), NullLogger<SdsSweep>.Instance);
        await sweep.RunSweepAsync("2026-07-07T00:00:00Z", default);

        Assert.Equal(SdsStatus.Fetched, mlStore.Items.Values.Single().Status);
        Assert.Single(reg.Items);
        Assert.True(search.Pushed.Count >= 10);
    }

    // ---- batch-robustness: one bad candidate/entry must never abort the whole sweep ----

    private sealed class BoomExtractor : IPdfTextExtractor
    {
        public string Extract(byte[] pdf)
        {
            var s = System.Text.Encoding.UTF8.GetString(pdf);
            if (s.Contains("BOOM")) throw new InvalidOperationException("corrupt pdf");
            return s;
        }
    }

    // Delegates to the real casTemplate strategy, but blows up resolving one specific CAS —
    // simulates a resolver-level failure (bad config regex, supplier search change, ...).
    private sealed class ThrowOnCasStrategy : ISourceStrategy
    {
        private readonly CasTemplateStrategy _inner = new();
        public string Name => "casTemplate";
        public Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
            AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
            => key.Cas == "1111-11-1"
                ? throw new InvalidOperationException("resolver boom")
                : _inner.ResolveAsync(entry, key, fetch, ct);
    }

    private static (MasterListRepo Repo, InMemoryMasterListStore Store, IngestionPipeline Pipe,
        InMemoryRegistryStore Reg, AllowlistProvider Allow) Rig(string allowJson)
    {
        var store = new InMemoryMasterListStore();
        var allow = AllowlistProvider.FromJson(allowJson);
        var reg = new InMemoryRegistryStore();
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new BoomExtractor(),
            new GhsChunker(), new FakeEmbedder(), new FakeSearchClient(), new RegistryRepo(reg),
            allow.Domains, new SdsOptions());
        return (new MasterListRepo(store), store, pipe, reg, allow);
    }

    [Fact]
    public async Task Sweep_tries_next_supplier_when_a_candidate_ingest_throws()
    {
        var (repo, store, pipe, reg, allow) = Rig("""
          [ { "supplier":"Corrupt","domain":"corrupt.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://corrupt.example/{cas}.pdf" },
            { "supplier":"Good","domain":"good.example","priority":20,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://good.example/{cas}.pdf" } ]
        """);
        await repo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var goodSds = File.ReadAllBytes("Resources/sample_sds.txt");
        var egress = new DryRunEgressClient(url =>
            url.Host == "corrupt.example"
                ? new EgressResult(System.Text.Encoding.UTF8.GetBytes("BOOM"), "application/pdf", url)
                : new EgressResult(goodSds, "application/pdf", url));

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var sweep = new SdsSweep(repo, resolver, egress, pipe, new SdsOptions(), NullLogger<SdsSweep>.Instance);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);

        Assert.Equal(SdsStatus.Fetched, store.Items.Values.Single().Status); // 2nd supplier won
        Assert.Single(reg.Items);
    }

    [Fact]
    public async Task Sweep_records_failure_and_moves_to_next_entry_when_all_candidates_throw()
    {
        var (repo, store, pipe, _, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        await repo.AppendAsync("Xx", "boom", "2222-22-2", null, "sweep", "2020-01-01T00:00:00Z", default);
        await repo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var goodSds = File.ReadAllBytes("Resources/sample_sds.txt");
        var egress = new DryRunEgressClient(url =>
            url.AbsolutePath.Contains("2222-22-2")
                ? new EgressResult(System.Text.Encoding.UTF8.GetBytes("BOOM"), "application/pdf", url)
                : new EgressResult(goodSds, "application/pdf", url));

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var sweep = new SdsSweep(repo, resolver, egress, pipe, new SdsOptions(), NullLogger<SdsSweep>.Instance);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);   // must not throw

        Assert.Equal(SdsStatus.Failed, store.Items["Xx_boom"].Status);
        Assert.Equal(1, store.Items["Xx_boom"].AttemptCount);
        Assert.Equal(SdsStatus.Fetched, store.Items["Na_hydroxide"].Status);
    }

    [Fact]
    public async Task Sweep_records_failure_and_continues_when_the_resolver_itself_throws()
    {
        var (repo, store, pipe, _, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        await repo.AppendAsync("Yy", "resolverboom", "1111-11-1", null, "sweep", "2020-01-01T00:00:00Z", default);
        await repo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var goodSds = File.ReadAllBytes("Resources/sample_sds.txt");
        var egress = new DryRunEgressClient(url => new EgressResult(goodSds, "application/pdf", url));

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new ThrowOnCasStrategy() });
        var sweep = new SdsSweep(repo, resolver, egress, pipe, new SdsOptions(), NullLogger<SdsSweep>.Instance);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);   // must not throw

        Assert.Equal(SdsStatus.Failed, store.Items["Yy_resolverboom"].Status);
        Assert.Equal(1, store.Items["Yy_resolverboom"].AttemptCount);
        Assert.Equal(SdsStatus.Fetched, store.Items["Na_hydroxide"].Status);
    }
}
