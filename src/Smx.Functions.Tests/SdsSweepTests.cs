using Smx.Functions.Sds.Acquisition;
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
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new TextExtractor(),
            new GhsChunker(), new FakeEmbedder(), search, new RegistryRepo(reg), new SdsOptions());

        var sweep = NewSweep(mlRepo, resolver, egress, pipe, reg);
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

    // Builds the sweep the way production does: over an SdsAcquirer, so these tests exercise the same
    // code path the operator's manual sync and an agent's ensure_sds run.
    private static SdsSweep NewSweep(MasterListRepo repo, SourceResolver resolver, IEgressClient egress,
        IngestionPipeline pipe, InMemoryRegistryStore reg, SdsOptions? opts = null)
    {
        var o = opts ?? new SdsOptions();
        var acquirer = new SdsAcquirer(repo, new RegistryRepo(reg), resolver, egress, pipe, o,
            NullLogger<SdsAcquirer>.Instance);
        return new SdsSweep(repo, acquirer, o, NullLogger<SdsSweep>.Instance);
    }

    private static (MasterListRepo Repo, InMemoryMasterListStore Store, IngestionPipeline Pipe,
        InMemoryRegistryStore Reg, AllowlistProvider Allow) Rig(string allowJson)
    {
        var store = new InMemoryMasterListStore();
        var allow = AllowlistProvider.FromJson(allowJson);
        var reg = new InMemoryRegistryStore();
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new BoomExtractor(),
            new GhsChunker(), new FakeEmbedder(), new FakeSearchClient(), new RegistryRepo(reg),
            new SdsOptions());
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
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);

        Assert.Equal(SdsStatus.Fetched, store.Items.Values.Single().Status); // 2nd supplier won
        Assert.Single(reg.Items);
    }

    [Fact]
    public async Task Sweep_records_failure_and_moves_to_next_entry_when_all_candidates_throw()
    {
        var (repo, store, pipe, reg, allow) = Rig("""
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
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);   // must not throw

        Assert.Equal(SdsStatus.Failed, store.Items["Xx_boom"].Status);
        Assert.Equal(1, store.Items["Xx_boom"].AttemptCount);
        Assert.Equal(SdsStatus.Fetched, store.Items["Na_hydroxide"].Status);
    }

    [Fact]
    public async Task Sweep_records_failure_and_continues_when_the_resolver_itself_throws()
    {
        var (repo, store, pipe, reg, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        await repo.AppendAsync("Yy", "resolverboom", "1111-11-1", null, "sweep", "2020-01-01T00:00:00Z", default);
        await repo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var goodSds = File.ReadAllBytes("Resources/sample_sds.txt");
        var egress = new DryRunEgressClient(url => new EgressResult(goodSds, "application/pdf", url));

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new ThrowOnCasStrategy() });
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);
        await sweep.RunSweepAsync("2026-07-16T00:00:00Z", default);   // must not throw

        Assert.Equal(SdsStatus.Failed, store.Items["Yy_resolverboom"].Status);
        Assert.Equal(1, store.Items["Yy_resolverboom"].AttemptCount);
        Assert.Equal(SdsStatus.Fetched, store.Items["Na_hydroxide"].Status);
    }

    // ---- bounded runs (the operator's "sync now") ----

    // What the bound leaves behind has to be REPORTED, not silently dropped: an operator who cannot tell
    // a finished sync from a truncated one has no way to know whether to run it again.
    [Fact]
    public async Task A_bounded_run_reports_what_it_left_behind()
    {
        var (repo, _, pipe, reg, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        for (var i = 0; i < 7; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "sweep", "2020-01-01T00:00:00Z", default);

        var egress = new DelegateEgressClient((_, _) => Task.FromResult<EgressResult?>(null));
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);

        var report = await sweep.RunSweepAsync("2026-07-29T00:00:00Z", maxEntries: 3,
            Timeout.InfiniteTimeSpan, default);

        Assert.Equal(3, report.Examined);
        Assert.Equal(4, report.Remaining);
        Assert.Equal(0, report.Fetched);
        Assert.Equal(3, report.Unavailable);
    }

    [Fact]
    public async Task An_unbounded_run_leaves_nothing_remaining()
    {
        var (repo, _, pipe, reg, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        for (var i = 0; i < 4; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "sweep", "2020-01-01T00:00:00Z", default);

        var egress = new DelegateEgressClient((_, _) => Task.FromResult<EgressResult?>(null));
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });

        var report = await NewSweep(repo, resolver, egress, pipe, reg)
            .RunSweepAsync("2026-07-29T00:00:00Z", default);

        Assert.Equal(4, report.Examined);
        Assert.Equal(0, report.Remaining);
    }

    // ---- concurrency ----

    /// Egress that runs an arbitrary delegate, so a test can observe timing rather than only outcomes.
    private sealed class DelegateEgressClient : IEgressClient
    {
        private readonly Func<Uri, CancellationToken, Task<EgressResult?>> _f;
        public DelegateEgressClient(Func<Uri, CancellationToken, Task<EgressResult?>> f) => _f = f;
        public Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct) => _f(url, ct);
    }

    private static void RecordPeak(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }

    // The 2026-07-16 sweep took 27 minutes against a 30-minute platform timeout because entries were
    // processed strictly serially behind 30-second fetch timeouts — it came within three minutes of
    // being killed mid-run. Concurrency is not an optimisation here; it is what keeps a full sweep
    // inside the host's budget as the master list grows.
    [Fact]
    public async Task Entries_are_processed_concurrently()
    {
        var (repo, _, pipe, reg, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        for (var i = 0; i < 8; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "sweep", "2020-01-01T00:00:00Z", default);

        var inFlight = 0; var peak = 0;
        var egress = new DelegateEgressClient(async (_, ct) =>
        {
            RecordPeak(ref peak, Interlocked.Increment(ref inFlight));
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref inFlight);
            return null;
        });

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);
        await sweep.RunSweepAsync("2026-07-29T00:00:00Z", default);

        Assert.True(peak > 1, $"expected concurrent processing; peak in-flight was {peak}");
    }

    // Concurrency must not weaken the per-entry isolation the serial loop had: every entry still gets
    // its own attempt recorded even when a neighbour's supplier explodes.
    [Fact]
    public async Task Concurrency_preserves_per_entry_isolation()
    {
        var (repo, store, pipe, reg, allow) = Rig("""
          [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
        """);
        await repo.AppendAsync("Boom", "x", "9999-99-9", null, "sweep", "2020-01-01T00:00:00Z", default);
        for (var i = 0; i < 4; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "sweep", "2020-01-01T00:00:00Z", default);

        var egress = new DelegateEgressClient((u, _) => u.AbsolutePath.Contains("9999-99-9")
            ? throw new InvalidOperationException("supplier exploded")
            : Task.FromResult<EgressResult?>(null));

        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var sweep = NewSweep(repo, resolver, egress, pipe, reg);
        await sweep.RunSweepAsync("2026-07-29T00:00:00Z", default);   // must not throw

        var all = await store.ListAllAsync(default);
        Assert.Equal(5, all.Count);
        Assert.All(all, e => Assert.Equal(SdsStatus.Failed, e.Status));
        Assert.All(all, e => Assert.Equal(1, e.AttemptCount));
    }
}
