using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Sds.Domain;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class SyncPipelineTests
{
    private const string RegistryJson = """
    [{ "sourceId":"oehha-prop65","regulation":"California Proposition 65","authority":"OEHHA",
       "accessMethod":"dataset","domain":"oehha.ca.gov","parser":"OehhaProp65Parser","enabled":true,"version":1,
       "documents":[{"docId":"p65-list","url":"https://oehha.ca.gov/list.csv","title":"P65"}] }]
    """;

    private const string CsvV1 = "Chemical,CAS No.,Date Listed\nLead,7439-92-1,10/01/1992\nBenzene,71-43-2,02/27/1987\n";
    private const string CsvV2 = CsvV1 + "Arsenic,7440-38-2,02/27/1987\n";

    private sealed class Harness
    {
        public FakeBronzeStore Bronze = new();
        public InMemoryRegStateStore State = new();
        public InMemoryRegSilverStore Silver = new();
        public InMemoryRegReviewStore Review = new();
        public InMemoryRegRunsStore Runs = new();
        public FakeRegSearchClient Search = new();
        public string Csv = CsvV1;

        public SyncPipeline Build(RegOptions opts)
        {
            var registry = RegRegistryProvider.FromJson(RegistryJson);
            IRegEgress egress = new RegDryRunEgress(url => new EgressResult(Encoding.UTF8.GetBytes(Csv), "text/csv", url));
            var bronzeIngestor = new BronzeIngestor(Bronze, State);
            var parsers = new RegParserRegistry(new IRegParser[] { new OehhaProp65Parser() });
            return new SyncPipeline(registry, egress, bronzeIngestor, parsers, Silver, State, Review, Runs,
                new FakeEmbedder(), Search, opts, NullLogger<SyncPipeline>.Instance);
        }
    }

    private static RegOptions NeverHold => new() { AnomalyDiffAbs = 1_000_000 };

    [Fact]
    public async Task Normal_run_auto_promotes_to_gold_with_citations()
    {
        var h = new Harness();
        var pipeline = h.Build(NeverHold);

        var diff = await pipeline.RunSyncAsync("2026-07-01T03:00:00Z", default);

        Assert.False(diff.Anomaly.Anomalous);
        Assert.Equal(2, h.Search.Pushed.Count);
        Assert.Equal(1, h.Search.EnsureCalls);
        Assert.Equal(RegStatus.AutoPromoted, h.Review.Items["sync-202607"].Status);
        // Every Gold chunk carries source + official_date + sync_date (§15).
        Assert.All(h.Search.Pushed, g =>
        {
            Assert.Equal("OEHHA", g.Authority);
            Assert.False(string.IsNullOrEmpty(g.OfficialDate));
            Assert.Equal("2026-07-01", g.SyncDate);
        });
        // Silver is live, state advanced.
        Assert.All(h.Silver.Items.Values, c => Assert.Equal("live", c.Status));
        Assert.NotNull(await h.State.GetAsync("p65-list", "oehha-prop65", default));
    }

    [Fact]
    public async Task Second_run_with_unchanged_content_is_a_noop()
    {
        var h = new Harness();
        await h.Build(NeverHold).RunSyncAsync("2026-07-01T03:00:00Z", default);
        var pushedAfterFirst = h.Search.Pushed.Count;

        var diff = await h.Build(NeverHold).RunSyncAsync("2026-08-01T03:00:00Z", default);

        Assert.Empty(diff.ChangedDocIds);
        Assert.Equal(1, diff.Unchanged);
        Assert.True(h.Search.Pushed.Count == pushedAfterFirst); // nothing re-pushed
    }

    [Fact]
    public async Task Changed_content_repromotes_only_the_changed_doc()
    {
        var h = new Harness();
        await h.Build(NeverHold).RunSyncAsync("2026-07-01T03:00:00Z", default);
        h.Csv = CsvV2; // one more chemical added upstream

        var diff = await h.Build(NeverHold).RunSyncAsync("2026-08-01T03:00:00Z", default);

        Assert.Equal(1, diff.Changed);
        Assert.Equal(3, h.Search.Pushed.Count(g => g.SyncDate == "2026-08-01")); // 3 chunks re-pushed for the changed doc
    }

    [Fact]
    public async Task Anomalous_run_is_held_and_not_promoted_then_approve_promotes()
    {
        var h = new Harness();
        var pipeline = h.Build(new RegOptions { AnomalyDiffAbs = 1 }); // any change trips the breaker

        var diff = await pipeline.RunSyncAsync("2026-07-01T03:00:00Z", default);

        Assert.True(diff.Anomaly.Anomalous);
        Assert.Equal(RegStatus.Held, h.Review.Items["sync-202607"].Status);
        Assert.Empty(h.Search.Pushed); // Gold NOT promoted while held

        // Operator approves → resume promotion (what ReviewDecisionHttp does).
        await pipeline.PromoteAsync("sync-202607", default);
        Assert.Equal(2, h.Search.Pushed.Count);
        Assert.All(h.Silver.Items.Values, c => Assert.Equal("live", c.Status));
    }

    [Fact]
    public async Task Rejected_run_discards_staged_silver()
    {
        var h = new Harness();
        var pipeline = h.Build(new RegOptions { AnomalyDiffAbs = 1 });
        await pipeline.RunSyncAsync("2026-07-01T03:00:00Z", default);

        await h.Silver.DiscardStagedAsync("sync-202607", default); // what ReviewDecisionHttp reject does

        Assert.Empty(h.Search.Pushed);
        Assert.All(h.Silver.Items.Values, c => Assert.Equal("superseded", c.Status));
    }
}
