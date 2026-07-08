using System.Text;
using System.Text.Json;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Reg.Sourcing;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class BronzeIngestorTests
{
    private static readonly RegSource Source = new(
        "oehha-prop65", "California Proposition 65", "OEHHA", "dataset", "oehha.ca.gov",
        "OehhaProp65Parser", true, 1, new[] { new RegDoc("p65-list", "https://oehha.ca.gov/list.csv", "P65") });

    private static RegDoc Doc => Source.Documents[0];

    private static BronzeIngestor Build(out FakeBronzeStore bronze, out InMemoryRegStateStore state)
    {
        bronze = new FakeBronzeStore();
        state = new InMemoryRegStateStore();
        return new BronzeIngestor(bronze, state);
    }

    [Fact]
    public async Task FirstFetch_is_added_and_writes_raw_plus_meta()
    {
        var ingestor = Build(out var bronze, out _);
        var egress = RegDryRunEgress.Default(Encoding.UTF8.GetBytes("chemical,cas\nLead,7439-92-1\n"), "text/csv");

        var outcome = await ingestor.FetchAndStageAsync(Source, Doc, egress, "sync-202607", "20260701T030000Z", default);

        Assert.Equal(DocResult.Added, outcome.Result);
        Assert.NotNull(outcome.Raw);
        Assert.Contains("regulatory/oehha-prop65/p65-list/20260701T030000Z/raw.csv", bronze.Blobs.Keys);
        Assert.Contains("regulatory/oehha-prop65/p65-list/20260701T030000Z/meta.json", bronze.Blobs.Keys);

        var meta = JsonSerializer.Deserialize<BronzeMeta>(
            bronze.Blobs["regulatory/oehha-prop65/p65-list/20260701T030000Z/meta.json"],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(outcome.Sha256, meta.Sha256);
        Assert.Equal("text/csv", meta.ContentType);
        Assert.Equal("sync-202607", meta.SyncRunId);
    }

    [Fact]
    public async Task Unchanged_content_is_skipped_when_state_matches()
    {
        var ingestor = Build(out var bronze, out var state);
        var bytes = Encoding.UTF8.GetBytes("chemical,cas\nLead,7439-92-1\n");
        var sha = BronzeIngestor.Sha256Hex(bytes);
        await state.UpsertAsync(new RegDocState("p65-list", "oehha-prop65", sha, "2026-06-01", "sync-202606", "old"), default);

        var outcome = await ingestor.FetchAndStageAsync(
            Source, Doc, RegDryRunEgress.Default(bytes, "text/csv"), "sync-202607", "20260701T030000Z", default);

        Assert.Equal(DocResult.Unchanged, outcome.Result);
        Assert.Null(outcome.Raw);
        Assert.Empty(bronze.Blobs); // nothing written on unchanged
    }

    [Fact]
    public async Task Changed_content_is_detected_when_sha_differs_from_state()
    {
        var ingestor = Build(out var bronze, out var state);
        await state.UpsertAsync(new RegDocState("p65-list", "oehha-prop65", "deadbeef", "2026-06-01", "sync-202606", "old"), default);

        var outcome = await ingestor.FetchAndStageAsync(
            Source, Doc, RegDryRunEgress.Default(Encoding.UTF8.GetBytes("new content"), "text/csv"),
            "sync-202607", "20260701T030000Z", default);

        Assert.Equal(DocResult.Changed, outcome.Result);
        Assert.NotNull(outcome.Raw);
        Assert.NotEmpty(bronze.Blobs);
    }

    [Fact]
    public async Task Failed_fetch_returns_error_and_writes_nothing()
    {
        var ingestor = Build(out var bronze, out _);
        var egress = new RegDryRunEgress(_ => null); // simulate blocked/failed fetch

        var outcome = await ingestor.FetchAndStageAsync(Source, Doc, egress, "sync-202607", "20260701T030000Z", default);

        Assert.Equal(DocResult.Error, outcome.Result);
        Assert.Empty(bronze.Blobs);
    }
}
