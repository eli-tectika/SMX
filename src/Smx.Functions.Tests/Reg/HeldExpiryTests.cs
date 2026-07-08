using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Sds.Domain;
using System.Text;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class HeldExpiryTests
{
    private const string RegistryJson = """
    [{ "sourceId":"s","regulation":"R","authority":"A","accessMethod":"dataset","domain":"oehha.ca.gov",
       "parser":"OehhaProp65Parser","enabled":true,"version":1,
       "documents":[{"docId":"d","url":"https://oehha.ca.gov/x.csv","title":"t"}] }]
    """;

    private static SyncPipeline Build(InMemoryRegReviewStore review, InMemoryRegSilverStore silver, RegOptions opts)
    {
        var registry = RegRegistryProvider.FromJson(RegistryJson);
        IRegEgress egress = new RegDryRunEgress(url => new EgressResult(Encoding.UTF8.GetBytes("Chemical,CAS No.\nLead,7439-92-1\n"), "text/csv", url));
        var bronze = new BronzeIngestor(new FakeBronzeStore(), new InMemoryRegStateStore());
        var parsers = new RegParserRegistry(new IRegParser[] { new OehhaProp65Parser() });
        return new SyncPipeline(registry, egress, bronze, parsers, silver, new InMemoryRegStateStore(),
            review, new InMemoryRegRunsStore(), new FakeEmbedder(), new FakeRegSearchClient(), opts,
            NullLogger<SyncPipeline>.Instance);
    }

    [Fact]
    public async Task Stale_held_run_is_expired_and_its_staged_silver_discarded()
    {
        var review = new InMemoryRegReviewStore();
        var silver = new InMemoryRegSilverStore();
        var opts = new RegOptions { HeldExpiryDays = 30, AnomalyDiffAbs = 1_000_000 };
        var pipeline = Build(review, silver, opts);

        // Simulate a held run 40 days ago with a staged chunk.
        var diff = new CorpusDiff("sync-202605", 0, 1, 0, 0, new[] { "d" }, new AnomalyAssessment(true, new[] { "big" }));
        await review.UpsertAsync(new ReviewRecord("sync-202605", "sync-202605", diff, RegStatus.Held, null, null, "2026-05-22T03:00:00Z", null), default);
        await silver.UpsertStagedAsync(new[] {
            new SilverChunk("d#0", "s", "d", 0, "x", new Citation("R","A",null,null,"u","2026-05-01"), "sha", "sync-202605", "2026-05-22", "staged") }, default);

        await pipeline.ExpireStaleHeldAsync(DateTimeOffset.Parse("2026-07-01T03:00:00Z"), default);

        Assert.Equal(RegStatus.HeldExpired, review.Items["sync-202605"].Status);
        Assert.All(silver.Items.Values.Where(c => c.SyncRunId == "sync-202605"), c => Assert.Equal("superseded", c.Status));
    }

    [Fact]
    public async Task Recent_held_run_is_not_expired()
    {
        var review = new InMemoryRegReviewStore();
        var opts = new RegOptions { HeldExpiryDays = 30 };
        var pipeline = Build(review, new InMemoryRegSilverStore(), opts);

        var diff = new CorpusDiff("sync-202706", 0, 1, 0, 0, new[] { "d" }, new AnomalyAssessment(true, new[] { "big" }));
        await review.UpsertAsync(new ReviewRecord("sync-202706", "sync-202706", diff, RegStatus.Held, null, null, "2026-06-28T03:00:00Z", null), default);

        await pipeline.ExpireStaleHeldAsync(DateTimeOffset.Parse("2026-07-01T03:00:00Z"), default);

        Assert.Equal(RegStatus.Held, review.Items["sync-202706"].Status);
    }
}
