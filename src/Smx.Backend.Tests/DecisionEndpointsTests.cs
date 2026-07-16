using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The VP hard gate over HTTP. The endpoint under test is the ONLY writer of an approved VP GateDoc
/// (the dispatcher's close handler trusts that — the same contract as the regulatory gate), so every 422
/// here is a false pass that never happened: a signature over a nonexistent code, over a partial
/// confirmation, over an unsigned or no-longer-covering regulatory analysis.
public class DecisionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly HttpClient _client;
    private const string P = "proj-vp-1";

    public DecisionEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
        })).CreateClient();

    // ---- fixtures --------------------------------------------------------------------------------------

    /// One finalized code per component. RatioSignature is DERIVED from the markers, so the tests read it
    /// off this record rather than hard-coding a string that could drift from the rendering.
    private static MarkerCode Code(string componentId) => componentId switch
    {
        "bottle" => new("bottle", [Marker("cas-zr", "Zr", 450.0), Marker("cas-y", "Y", 200.0)], "ratio 9:4"),
        _ => new(componentId, [Marker("cas-zr", "Zr", 300.0), Marker("cas-y", "Y", 300.0)], "ratio 1:1"),
    };

    private static CodeMarker Marker(string cas, string element, double ppm) =>
        new(cas, element, ppm, MetalLoading: 0.74, ElementMassMg: 1.0, CompoundMassMg: 1.35);

    private static string Ratio(string componentId) => Code(componentId).RatioSignature;

    private static ComponentDecision Component(string id) => new(
        id,
        Rows:
        [
            new DecisionRow("cas-zr", "Zr", Determinations.Recommended, 450.0,
                new ClearedCriteria(true, true, true),
                new TraceRefs(RecordIds.Verdict(P, "cas-zr", id), RecordIds.Dosing(P), RecordIds.Cost(P))),
        ],
        ProposedCode: new ProposedCode(Ratio(id), ["cas-zr", "cas-y"], "the agent's rationale — history, not a signature"));

    private static VerdictDoc Verdict(string cas, string element, string componentId = "bottle") => new()
    {
        Id = RecordIds.Verdict(P, cas, componentId), ProjectId = P, Cas = cas, ComponentId = componentId,
        Element = element, Form = "f",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true, Determination = Determinations.Recommended, DeterminationReason = "ruled",
    };

    /// The full pre-VP record: project parked awaiting-VP, the LIVE analysis (candidates + reviewed
    /// verdicts) the regulatory signature covers, the approved regulatory gate, dosing with one finalized
    /// code per component, and the DecisionDoc carrying the agent's proposals.
    private async Task SeedAwaitingVpAsync(params string[] componentIds)
    {
        if (componentIds.Length == 0) componentIds = ["bottle"];

        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in Stages.All) p.Stages[s].Status = "done";
        p.Stages[Stages.Decision].Status = "awaiting-VP";
        await _store.UpsertProjectAsync(p);

        var candidates = new CandidatesDoc { Id = RecordIds.Candidates(P), ProjectId = P };
        foreach (var id in componentIds)
        {
            candidates.Substances.Add(new CandidateSubstance(id, "Zr", "f", "cas-zr", null, null, false, "A", "s", []));
            candidates.Substances.Add(new CandidateSubstance(id, "Y", "f", "cas-y", null, null, false, "A", "s", []));
            await _store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", id));
            await _store.UpsertVerdictAsync(Verdict("cas-y", "Y", id));
        }
        await _store.UpsertCandidatesAsync(candidates);

        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t",
            Codes = [.. componentIds.Select(Code)],
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t",
            Components = [.. componentIds.Select(Component)],
        });
    }

    private static object Approve(string reason, params (string ComponentId, string Code)[] confirmations) => new
    {
        determination = "approved",
        reason,
        confirmations = confirmations.Select(c => new { componentId = c.ComponentId, code = c.Code }).ToArray(),
    };

    // ---- the signature ---------------------------------------------------------------------------------

    [Fact]
    public async Task PostDetermination_SignsTheGate_AndStampsConfirmations()
    {
        await SeedAwaitingVpAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed against the matrix on 16 Jul", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("approved", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var gate = await _store.GetGateAsync(P, GateTypes.Vp);
        Assert.Equal("approved", gate!.Status);
        Assert.False(string.IsNullOrEmpty(gate.ApprovedAt));

        var comp = Assert.Single((await _store.GetDecisionAsync(P))!.Components);
        Assert.Equal(Ratio("bottle"), comp.ConfirmedCode);
        Assert.Equal("VP R&D", comp.ConfirmedBy);
        Assert.Equal("codes reviewed against the matrix on 16 Jul", comp.ConfirmedReason);
        // The proposal is HISTORY, not overwritten — the audit trail keeps what the agent said.
        Assert.NotNull(comp.ProposedCode);
        Assert.Equal("the agent's rationale — history, not a signature", comp.ProposedCode!.Rationale);
    }

    [Fact]
    public async Task PostDetermination_RequiresAReason_422()
    {
        await SeedAwaitingVpAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("   ", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));                       // nothing written
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("Approved")]   // case matters — the dispatcher's Vp arm matches the exact literal
    public async Task PostDetermination_ThatIsNeitherApprovedNorRejected_422(string determination)
    {
        await SeedAwaitingVpAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            new { determination, reason = "a reason", confirmations = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
    }

    [Fact]
    public async Task PostDetermination_RefusesAnUnknownCode_422()
    {
        // A signature over a nonexistent code IS the false pass: the ratio names a code no DosingDoc holds,
        // so nothing downstream could ever trace it. 422 naming component+code; NO gate write, NO stamp.
        await SeedAwaitingVpAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("looks right", ("bottle", "Zr:Y = 1.00:0.99")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("bottle", body);
        Assert.Contains("Zr:Y = 1.00:0.99", body);
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Fact]
    public async Task PostDetermination_RefusesAPartialConfirmation_422()
    {
        // Gates table: "all components have a selected code". Confirming bottle but not label is a
        // signature over half a product — 422 naming the missing component, and NOTHING stamped, not even
        // the component that was confirmed (a 4xx means nothing happened).
        await SeedAwaitingVpAsync("bottle", "label");
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("bottle looks right", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("label", await res.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
        Assert.All((await _store.GetDecisionAsync(P))!.Components, c => Assert.Null(c.ConfirmedCode));
    }

    [Fact]
    public async Task PostDetermination_RefusesWhileRegulatoryUnsigned_422()
    {
        // VpGate.Armable's blocker surfaces verbatim: a VP signature over an unsigned compliance analysis
        // would stack one gate on a void.
        await SeedAwaitingVpAsync();
        var regGate = (await _store.GetGateAsync(P, GateTypes.Regulatory))!;
        regGate.Status = "locked";
        regGate.ApprovedAt = null;
        await _store.UpsertGateAsync(regGate);

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("sign it anyway", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("regulatory gate is not approved", await res.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
    }

    [Fact]
    public async Task PostDetermination_RefusesWhenTheRegulatorySignatureNoLongerCoversTheAnalysis_422()
    {
        // The gate record carries no binding to the verdicts it was signed over (the TryDoseAsync
        // rationale, StageDispatcher ~:207-228). A live unreviewed non-pass verdict that appeared AFTER the
        // regulatory approval means the signature no longer covers the analysis the VP would be signing
        // over — the VP gate must block, with the blocker surfaced.
        await SeedAwaitingVpAsync();
        var candidates = (await _store.GetCandidatesAsync(P))!;
        candidates.Substances.Add(new CandidateSubstance("bottle", "Ba", "f", "cas-ba", null, null, false, "A", "s", []));
        await _store.UpsertCandidatesAsync(candidates);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-ba", "bottle"), ProjectId = P, Cas = "cas-ba", ComponentId = "bottle",
            Element = "Ba", Form = "f",
            Dimensions = [new("ElementGate", VerdictStatus.Fail, [new Citation("regulatory", "x", "t")], 0.9, "fails")],
            // EvidenceReviewed = false — flagged, live, and nobody has looked at it since the signature.
        });

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("sign it anyway", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("cas-ba", await res.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Fact]
    public async Task PostDetermination_Rejected_RecordsTheRejection()
    {
        // The audit trail must show the VP looked and said no: the gate lands `locked` WITH the reason,
        // and the DecisionDoc is untouched — a rejection confirms nothing.
        await SeedAwaitingVpAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            new { determination = "rejected", reason = "the label code's ratio is too close to project X's" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var gate = await _store.GetGateAsync(P, GateTypes.Vp);
        Assert.Equal("locked", gate!.Status);
        Assert.Equal("the label code's ratio is too close to project X's", gate.Reason);
        Assert.Null(gate.ApprovedAt);
        var comp = Assert.Single((await _store.GetDecisionAsync(P))!.Components);
        Assert.Null(comp.ConfirmedCode);
        Assert.NotNull(comp.ProposedCode);   // the proposal survives the rejection — it is history
    }

    [Fact]
    public async Task PostDetermination_ApproveTwice_PreservesApprovedAt()
    {
        await SeedAwaitingVpAsync();
        var body = Approve("codes reviewed", ("bottle", Ratio("bottle")));

        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination", body);
        var first = (await _store.GetGateAsync(P, GateTypes.Vp))!.ApprovedAt;
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination", body);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(first, (await _store.GetGateAsync(P, GateTypes.Vp))!.ApprovedAt);
    }

    // ---- the gate read ---------------------------------------------------------------------------------

    [Fact]
    public async Task GetGateVp_ReportsStatusArmableBlockers()
    {
        // Mirror of GET /gate/regulatory: before the decision exists the gate is locked, not armable, and
        // the blocker says exactly what is missing.
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(p);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.Equal("locked", g.GetProperty("status").GetString());
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("decision has not run", g.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task GetGateVp_ReportsArmable_ThenApproved_AfterTheDetermination()
    {
        await SeedAwaitingVpAsync();

        var before = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");
        Assert.Equal("locked", before.GetProperty("status").GetString());
        Assert.True(before.GetProperty("armable").GetBoolean());

        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed", ("bottle", Ratio("bottle"))));

        var after = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");
        Assert.Equal("approved", after.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(after.GetProperty("approvedAt").GetString()));
    }
}
