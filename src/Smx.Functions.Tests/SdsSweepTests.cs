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
}
