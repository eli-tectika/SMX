using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Sds.Domain;
using Xunit;

namespace Smx.Functions.Tests.Reg;

// The monthly timer sweep must process only monthly-cadence (or null-cadence) sources; quarterly/static
// sources are skipped so they can be swept on their own schedule without the monthly run touching them.
public class CadenceFilterTests
{
    // Two enabled sources with the same parser + a shared CSV responder — distinguished only by cadence.
    private const string RegistryJson = """
    [
      { "sourceId":"src-monthly","regulation":"Monthly Reg","authority":"AUTH","accessMethod":"dataset",
        "domain":"oehha.ca.gov","parser":"OehhaProp65Parser","enabled":true,"version":1,"cadence":"monthly",
        "documents":[{"docId":"doc-monthly","url":"https://oehha.ca.gov/monthly.csv","title":"M"}] },
      { "sourceId":"src-quarterly","regulation":"Quarterly Reg","authority":"AUTH","accessMethod":"dataset",
        "domain":"oehha.ca.gov","parser":"OehhaProp65Parser","enabled":true,"version":1,"cadence":"quarterly",
        "documents":[{"docId":"doc-quarterly","url":"https://oehha.ca.gov/quarterly.csv","title":"Q"}] }
    ]
    """;

    private const string Csv = "Chemical,CAS No.,Date Listed\nLead,7439-92-1,10/01/1992\nBenzene,71-43-2,02/27/1987\n";

    private static SyncPipeline Build(out FakeRegSearchClient search, out InMemoryRegStateStore state)
    {
        search = new FakeRegSearchClient();
        state = new InMemoryRegStateStore();
        var registry = RegRegistryProvider.FromJson(RegistryJson);
        IRegEgress egress = new RegDryRunEgress(url => new EgressResult(Encoding.UTF8.GetBytes(Csv), "text/csv", url));
        var bronze = new BronzeIngestor(new FakeBronzeStore(), state);
        var parsers = new RegParserRegistry(new IRegParser[] { new OehhaProp65Parser() });
        return new SyncPipeline(registry, egress, bronze, parsers, new InMemoryRegSilverStore(), state,
            new InMemoryRegReviewStore(), new InMemoryRegRunsStore(), new FakeEmbedder(), search,
            new RegOptions { AnomalyDiffAbs = 1_000_000 }, NullLogger<SyncPipeline>.Instance);
    }

    [Fact]
    public async Task Monthly_sweep_processes_only_monthly_cadence_sources()
    {
        var pipeline = Build(out var search, out var state);

        var diff = await pipeline.RunSyncAsync("2026-07-01T03:00:00Z", default);

        // Only the monthly source's doc is fetched/parsed/promoted (2 chunks); the quarterly source is skipped.
        Assert.Equal(new[] { "doc-monthly" }, diff.ChangedDocIds.ToArray());
        Assert.Equal(2, search.Pushed.Count);
        Assert.NotNull(await state.GetAsync("doc-monthly", "src-monthly", default));
        Assert.Null(await state.GetAsync("doc-quarterly", "src-quarterly", default)); // never touched
    }
}
