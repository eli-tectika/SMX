using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// GET /projects/{id}/table — the one projection both the UI and the XLSX export read.
public class TableEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-table-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public TableEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IRecordStore>(_store))).CreateClient();

    private async Task SeedThroughDiscoveryAsync()
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        p.Stages[Stages.Intake].Status = StageStatus.Done;
        p.Stages[Stages.Discovery].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(p);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances =
            [
                new("bottle", "Ce", "oxide", "1306-38-3", null, null, true, "A", "stable in melt",
                    [new Citation("catalog", "ref-catalog/x", "t")]),
            ],
        });
    }

    [Fact]
    public async Task Get_404sForAnUnknownProject()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync("/projects/nope/table")).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsRowsWithOnlyTheDiscoveryGroup_WhenNothingDownstreamHasRun()
    {
        // 200 WITH ROWS, not 404. An analysis in progress is a state, not a missing resource -- and a 404
        // here would make the table useless for exactly the projects an operator most wants to watch.
        await SeedThroughDiscoveryAsync();

        var body = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/table");

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal("bottle", row.GetProperty("componentId").GetString());
        Assert.Equal("1306-38-3", row.GetProperty("cas").GetString());
        Assert.Equal("A", row.GetProperty("discovery").GetProperty("tier").GetString());

        // The downstream groups are present-and-null on the wire, so the UI reads "not reached" off the
        // response rather than inferring it from an absent key.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("regulatory").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("dosing").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("stoppedAt").ValueKind);
    }

    [Fact]
    public async Task Get_ReportsAStoppedRow_WithItsPhaseAndReason()
    {
        await SeedThroughDiscoveryAsync();
        var p = (await _store.GetProjectAsync(P))!;
        p.Stages[Stages.Regulatory].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(p);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "1306-38-3", "bottle"), ProjectId = P,
            Cas = "1306-38-3", ComponentId = "bottle", Element = "Ce", Form = "oxide",
            Dimensions = [new("ElementGate", VerdictStatus.Fail, [new Citation("regulatory", "x", "t")], 0.9, "r")],
            EvidenceReviewed = true,
            Determination = Determinations.Rejected,
            DeterminationReason = "element gate failed product-wide",
        });

        var body = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/table");

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal("regulatory", row.GetProperty("stoppedAt").GetString());
        Assert.Equal("element gate failed product-wide", row.GetProperty("stoppedReason").GetString());
    }
}
