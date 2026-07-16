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

    // ---- (d): a signature answers a park — 422 unless Stages[decision] == awaiting-VP ---------------------

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task PostDetermination_WhileTheDecisionIsMidRePick_Refuses_NothingStamped(string determination)
    {
        // Task 15(a) resets Decision to `pending` on a Dosing revision while the STALE DecisionDoc is
        // still on file. Without the stage guard the VP could sign that stale proposal — and the in-flight
        // re-pick would then OVERWRITE the stamped doc under an approved gate: close finds zero confirmed
        // codes and procurement releases over an empty conclusion. The park is the precondition.
        await SeedAwaitingVpAsync();
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "pending";   // mid-re-pick: reset, stale doc on file
        await _store.UpsertProjectAsync(project);

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            determination == "approved"
                ? Approve("sign the stale one", ("bottle", Ratio("bottle")))
                : new { determination = "rejected", reason = "no" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("pending", body);                     // the blocker names the actual status
        Assert.Contains("awaiting-VP", body);                 // and the park a signature answers
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));                       // nothing written
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task PostDetermination_AfterClose_Refuses_ApproveAndRejectAlike(string determination)
    {
        // Decision `done` means the VP signed and the project CLOSED — Marker Library written, conclusion
        // filed, procurement Released. A post-close approve would re-stamp history; a post-close REJECT is
        // the "revocation that revokes nothing": the gate would flip locked while Procurement stays
        // Released. Both are refused, and the signed gate is untouched either way.
        await SeedAwaitingVpAsync();
        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed", ("bottle", Ratio("bottle"))));         // the real signature
        var signedAt = (await _store.GetGateAsync(P, GateTypes.Vp))!.ApprovedAt;
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "done";                      // what the close dispatch stamps
        await _store.UpsertProjectAsync(project);
        var decision = (await _store.GetDecisionAsync(P))!;
        decision.Procurement.Status = ProcurementStatus.Released;             // ...and releases
        await _store.UpsertDecisionAsync(decision);

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            determination == "approved"
                ? Approve("stamp it again", ("bottle", Ratio("bottle")))
                : new { determination = "rejected", reason = "revoke it" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("done", await res.Content.ReadAsStringAsync());
        // The close is HISTORY: the gate is still approved with the ORIGINAL timestamp — in particular the
        // reject did NOT lock it over Released procurement — and the confirmations still stand.
        var gate = (await _store.GetGateAsync(P, GateTypes.Vp))!;
        Assert.Equal("approved", gate.Status);
        Assert.Equal(signedAt, gate.ApprovedAt);
        var comp = Assert.Single((await _store.GetDecisionAsync(P))!.Components);
        Assert.Equal(Ratio("bottle"), comp.ConfirmedCode);
        Assert.Equal(ProcurementStatus.Released, (await _store.GetDecisionAsync(P))!.Procurement.Status);
    }

    [Fact]
    public async Task GetGateVp_WhileTheStageIsNotParked_IsNotArmable_AndNamesTheBlocker()
    {
        // The read must never advertise what the POST refuses (the Task-13 lesson, applied to (d)): with
        // everything else armable but the stage mid-re-pick, `armable: true` here would be the lying
        // affordance that gets a stale proposal signed.
        await SeedAwaitingVpAsync();
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "pending";
        await _store.UpsertProjectAsync(project);

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.False(g.GetProperty("armable").GetBoolean());
        var blockers = g.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("'pending'") && b.Contains("not 'awaiting-VP'"));
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

    [Fact]
    public async Task GetGateVp_WithNoCandidatesOnFile_IsNotArmable_AndNamesTheBlocker()
    {
        // The read must report the same blocker the POST enforces: with an approved regulatory gate and a
        // proposing decision but NO candidates, the POST 422s "no candidates on file" — a read that says
        // `armable: true` over that state is the lying affordance that gets a gate rubber-stamped.
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t", Components = [Component("bottle")],
        });

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("no candidates on file", g.GetProperty("blockers").ToString());
    }

    // ---- the decision read (Task 13) ---------------------------------------------------------------------

    [Fact]
    public async Task GetDecision_404UntilTheDecisionExists()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/decision")).StatusCode);
    }

    [Fact]
    public async Task GetDecision_ProposalAndSignatureAreDistinctOnTheWire()
    {
        // Law 9 legible to the UI: proposedCode and confirmedCode both serialize camelCase, and an
        // UNCONFIRMED decision shows an EXPLICIT confirmedCode: null — the frontend must be able to tell
        // "proposed" from "signed" by reading the wire, never by guessing at an absent key.
        await SeedAwaitingVpAsync();

        var resp = await _client.GetAsync($"/projects/{P}/decision");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var comp = doc.GetProperty("components")[0];

        Assert.Equal(Ratio("bottle"), comp.GetProperty("proposedCode").GetProperty("ratioSignature").GetString());
        Assert.True(comp.TryGetProperty("confirmedCode", out var confirmed),
            "confirmedCode must be PRESENT on the wire even while null");
        Assert.Equal(JsonValueKind.Null, confirmed.ValueKind);

        // ... and once the VP signs, the same key carries the signature.
        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed", ("bottle", Ratio("bottle"))));
        var signed = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/decision");
        Assert.Equal(Ratio("bottle"), signed.GetProperty("components")[0].GetProperty("confirmedCode").GetString());
    }

    // ---- MSDS-before-order (Task 10): the last hard precondition ---------------------------------------

    /// A closed project: VP-confirmed codes and released procurement — what the close dispatch leaves behind.
    private async Task SeedReleasedAsync()
    {
        await SeedAwaitingVpAsync();
        var decision = (await _store.GetDecisionAsync(P))!;
        decision.Components = [.. decision.Components.Select(c => c with
        {
            ConfirmedCode = Ratio(c.ComponentId), ConfirmedBy = "VP R&D", ConfirmedReason = "reviewed",
        })];
        decision.Procurement.Status = ProcurementStatus.Released;
        await _store.UpsertDecisionAsync(decision);
    }

    private async Task SeedReviewedMsdsAsync(string cas) =>
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds(cas), Cas = cas, Supplier = "Acme Chemicals", Version = "3.1",
            Date = "2026-05-01", ReviewStatus = MsdsReviewStatus.Reviewed, ReviewedAt = "2026-07-15T00:00:00Z",
        });

    [Fact]
    public async Task PostOrder_BeforeTheVpGate_Is422()
    {
        // Procurement is a state flag (§4) and only the close dispatch releases it — an order before the
        // VP signature is an order for a product nobody approved.
        await SeedAwaitingVpAsync();   // decision exists, procurement still unreleased
        await SeedReviewedMsdsAsync("cas-zr");

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("procurement is not released", await res.Content.ReadAsStringAsync());
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_WithoutAReviewedMsds_Is422()
    {
        // THE hard precondition (§4: MSDS-before-order gates an individual order). No MsdsRegistryDoc, or
        // one still unreviewed — either way the order must not exist. 422 names the cas and the rule.
        await SeedReleasedAsync();
        // an entry EXISTS but nobody signed the review — currency is the operator's signature, not the file
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("cas-zr"), Cas = "cas-zr", Supplier = "Acme Chemicals", Version = "3.1",
            Date = "2026-05-01", ReviewStatus = MsdsReviewStatus.Unreviewed,
        });

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("cas-zr", body);
        Assert.Contains("MSDS-before-order", body);
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        // and an entirely ABSENT registry entry blocks identically
        var res2 = await _client.PostAsync($"/projects/{P}/orders/cas-y", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res2.StatusCode);
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_ForACasOutsideTheConfirmedCodes_Is422()
    {
        // You cannot order what the VP did not sign — even with a perfectly reviewed MSDS on file.
        await SeedReleasedAsync();
        await SeedReviewedMsdsAsync("cas-ba");   // reviewed, but in NO confirmed code

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-ba", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("cas-ba", await res.Content.ReadAsStringAsync());
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_WithReviewedMsds_RecordsTheOrder()
    {
        await SeedReleasedAsync();
        await SeedReviewedMsdsAsync("cas-zr");

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        // idempotent: re-POST is 202 and still ONE entry — an order is a record, not a counter
        var again = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }
}
