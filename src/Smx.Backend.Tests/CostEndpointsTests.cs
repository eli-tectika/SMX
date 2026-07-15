using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// GET /projects/{id}/cost — the read surface over the deterministic supplier-price audit. The two facts
/// that matter: the audit only exists AFTER the Cost stage has run (404 before), and every figure it hands
/// back carries the citation to the catalog listing it came from (procurement acts on these numbers and must
/// be able to check them).
public class CostEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "p-cost";
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public CostEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private async Task SeedCostedProjectAsync() =>
        await _store.UpsertCostAsync(new CostDoc
        {
            Id = RecordIds.Cost(P), ProjectId = P, GeneratedAt = "2026-07-15T00:00:00Z",
            Substances =
            [
                new SupplierAudit("1314-36-9", "Y", ["Acme Chem"],
                    new PriceQuote(0.42, "USD", "Acme Chem", "100 g",
                        new Citation("ref-catalog", "ref-catalog/acme-y2o3-100g", "2026-07-15T00:00:00Z")),
                    "cheapest of 1 parseable", []),
            ],
        });

    [Fact]
    public async Task GetCost_ReturnsTheAudit_WithEveryFigureCited()
    {
        await SeedCostedProjectAsync();
        var cost = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/cost");
        var line = cost.GetProperty("substances")[0];
        Assert.StartsWith("ref-catalog/", line.GetProperty("bestQuote").GetProperty("citation")
            .GetProperty("reference").GetString());
    }

    [Fact]
    public async Task GetCost_IsNotFound_BeforeTheStageHasRun() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/cost")).StatusCode);
}
