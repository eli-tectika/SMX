using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Xunit;

public class IngestionPipelineTests
{
    private sealed class TextExtractor : IPdfTextExtractor
    { public string Extract(byte[] pdf) => System.Text.Encoding.UTF8.GetString(pdf); }

    private static IngestionPipeline Build(out FakeBronzeStore bronze, out InMemoryRegistryStore reg,
        out FakeSearchClient search, out IReadOnlySet<string> domains)
    {
        bronze = new FakeBronzeStore(); reg = new InMemoryRegistryStore(); search = new FakeSearchClient();
        domains = new HashSet<string> { "sigmaaldrich.com" };
        return new IngestionPipeline(bronze, new SdsValidator(10), new TextExtractor(), new GhsChunker(),
            new FakeEmbedder(), search, new RegistryRepo(reg), domains, new SdsOptions());
    }

    private static SdsMetadata Meta() => new("1310-73-2", "Sigma-Aldrich", "Sodium hydroxide",
        "2024-03-01", "US", "en", "https://www.sigmaaldrich.com/US/en/sds/sigald/329460", "Na_hydroxide");

    [Fact]
    public async Task Valid_sds_lands_bronze_indexes_and_upserts_pointer()
    {
        var pipe = Build(out var bronze, out var reg, out var search, out _);
        var pdf = File.ReadAllBytes("Resources/sample_sds.txt");
        var r = await pipe.IngestAsync(pdf, Meta(), "sigmaaldrich.com", default);

        Assert.True(r.Ok);
        Assert.Single(bronze.Blobs);
        Assert.Equal("1310-73-2|sigma-aldrich|2024-03-01", r.RegistryId);
        Assert.True(search.Pushed.Count >= 10);            // GHS chunks pushed
        Assert.Equal(1, search.EnsureCalls);
        var pointer = reg.Items.Values.Single();
        Assert.True(pointer.Indexed);
        Assert.Equal(search.Pushed.Count, pointer.IndexDocIds.Count);
    }

    [Fact]
    public async Task Invalid_cas_is_rejected_and_nothing_indexed()
    {
        var pipe = Build(out _, out var reg, out var search, out _);
        var pdf = File.ReadAllBytes("Resources/sample_sds.txt");
        var r = await pipe.IngestAsync(pdf, Meta() with { Cas = "7440-02-0" }, "sigmaaldrich.com", default);
        Assert.False(r.Ok);
        Assert.Empty(search.Pushed);
        Assert.Empty(reg.Items);
    }
}
