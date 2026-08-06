using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// POST /projects/{id}/amendments — the operator changing a REQUIREMENT after intake.
///
/// Distinct from record_answer (a pre-intake gap-fill that refuses once constraints exist) and from
/// apply_revision (which revises an agent's output on one stage). Law 4 survives: the operator states the
/// new requirement and WHY, and the agents re-derive everything downstream.
public class AmendmentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-amend-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public AmendmentEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IRecordStore>(_store))).CreateClient();

    private async Task SeedAsync(bool regulatorySigned = false)
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in Stages.All) p.Stages[s].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(p);
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "PET", "food contact", ["EU"], "brand protection", 250.0)],
        });
        if (regulatorySigned)
            await _store.UpsertGateAsync(new GateDoc
            {
                Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P,
                GateType = GateTypes.Regulatory, Status = "approved",
                ApprovedAt = "2026-08-01T00:00:00.0000000+00:00", ApprovedBy = "operator",
            });
    }

    private Task<HttpResponseMessage> AmendAsync(
        string field, string value, string? reason = "the customer confirmed it", bool confirm = false) =>
        _client.PostAsJsonAsync($"/projects/{P}/amendments", new
        {
            field, value, reason, componentId = "bottle", confirmSignatureVoid = confirm,
        });

    [Fact]
    public async Task Amend_PatchesTheConstraints_AndLogsFromToAndWhy()
    {
        await SeedAsync();

        var res = await AmendAsync("markets", "EU, US, JP", "customer added Japan on the 6 Aug call");

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var constraints = await _store.GetConstraintsAsync(P);
        Assert.Equal(["EU", "US", "JP"], Assert.Single(constraints!.Components).Markets);

        var log = Assert.Single((await _store.GetProjectAsync(P))!.Amendments);
        Assert.Equal("markets", log.Field);
        Assert.Equal("EU", log.From);                 // what it WAS, so the log reads as a history
        Assert.Equal("EU, US, JP", log.To);
        Assert.Equal("customer added Japan on the 6 Aug call", log.Reason);
    }

    [Fact]
    public async Task Amend_ResetsOnlyTheStagesInTheBlastRadius()
    {
        // The whole point of RerunScope. Markets touch the regulatory lane; Discovery's candidates do not
        // depend on them, and re-running Discovery would be minutes of Foundry time for an identical answer.
        await SeedAsync();

        await AmendAsync("markets", "EU, JP");

        var stages = (await _store.GetProjectAsync(P))!.Stages;
        Assert.Equal(StageStatus.Pending, stages[Stages.Regulatory].Status);
        Assert.Equal(StageStatus.Pending, stages[Stages.Matrix].Status);
        Assert.Equal(StageStatus.Pending, stages[Stages.Decision].Status);
        Assert.Equal(StageStatus.Done, stages[Stages.Discovery].Status);
        Assert.Equal(StageStatus.Done, stages[Stages.Dosing].Status);
    }

    [Fact]
    public async Task Amend_ABatchMass_TouchesDosingOnly_AndLeavesRegulatoryAlone()
    {
        await SeedAsync(regulatorySigned: true);

        var res = await AmendAsync("batchMassKg", "500");

        // NO confirmation needed: the scope does not reach Regulatory, so the R.E.'s signature is not at
        // risk. A prompt here would be crying wolf, and a prompt that cries wolf gets clicked through.
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("approved", (await _store.GetGateAsync(P, GateTypes.Regulatory))!.Status);

        var stages = (await _store.GetProjectAsync(P))!.Stages;
        Assert.Equal(StageStatus.Pending, stages[Stages.Dosing].Status);
        Assert.Equal(StageStatus.Done, stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task Amend_RefusesWithoutConfirmation_WhenItWouldVoidASignature()
    {
        await SeedAsync(regulatorySigned: true);

        var res = await AmendAsync("markets", "EU, JP");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(GateTypes.Regulatory,
            body.GetProperty("voids").EnumerateArray().Select(e => e.GetString()));

        // NOTHING was written: the constraints, the gate and the stages all stand.
        Assert.Equal(["EU"], Assert.Single((await _store.GetConstraintsAsync(P))!.Components).Markets);
        Assert.Equal("approved", (await _store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
        Assert.Empty((await _store.GetProjectAsync(P))!.Amendments);
    }

    [Fact]
    public async Task Amend_WithConfirmation_VoidsTheSignatureAsAPair_AndRecordsThat()
    {
        await SeedAsync(regulatorySigned: true);

        var res = await AmendAsync("markets", "EU, JP", confirm: true);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var gate = (await _store.GetGateAsync(P, GateTypes.Regulatory))!;
        Assert.Equal("locked", gate.Status);
        // The PAIR moves together. A locked gate still carrying a signer reports
        // {status:"locked", approvedBy:"operator"} -- which a screen that renders the signer whenever it is
        // non-null prints as "signed by the operator" over a gate this amendment deliberately voided.
        Assert.Null(gate.ApprovedAt);
        Assert.Null(gate.ApprovedBy);

        var log = Assert.Single((await _store.GetProjectAsync(P))!.Amendments);
        Assert.Contains(GateTypes.Regulatory, log.VoidedSignatures);
    }

    [Fact]
    public async Task Amend_RequiresAReason()
    {
        await SeedAsync();

        var res = await AmendAsync("markets", "EU, JP", reason: "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty((await _store.GetProjectAsync(P))!.Amendments);
    }

    [Fact]
    public async Task Amend_RefusesTheMeasuredData_ByConstruction()
    {
        // The physicist's numbers are not a requirement the operator can restate. They are measurements, and
        // if they are wrong the answer is a re-measurement, not an amendment.
        await SeedAsync();

        foreach (var field in new[] { "measuredBackground", "device", "elementPools" })
        {
            var res = await AmendAsync(field, "whatever");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        }
        Assert.Empty((await _store.GetProjectAsync(P))!.Amendments);
    }

    [Fact]
    public async Task Amend_RefusesEmptyMarkets_BecauseThatEmptiesTheRegulatoryScreen()
    {
        await SeedAsync();

        var res = await AmendAsync("markets", "  ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("passes everything", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Amend_BeforeIntakeHasRun_SaysSo_RatherThanInventingConstraints()
    {
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(p);   // no constraints

        var res = await AmendAsync("markets", "EU");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("intake has not run", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ReturnsTheLog_InOrder()
    {
        await SeedAsync();
        await AmendAsync("markets", "EU, JP", "first");
        await AmendAsync("application", "closure", "second");

        var body = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/amendments");

        var reasons = body.GetProperty("amendments").EnumerateArray()
            .Select(a => a.GetProperty("reason").GetString()).ToList();
        Assert.Equal(["first", "second"], reasons);
    }
}
