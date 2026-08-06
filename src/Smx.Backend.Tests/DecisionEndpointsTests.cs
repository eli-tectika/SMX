using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// THE hard gate over HTTP - the only one left (16.4). The endpoint under test is the ONLY writer of an
/// approved VP GateDoc (CloseProjectAsync trusts that and re-checks nothing), so every 422 here is a false
/// pass that never happened: a signature over a nonexistent code, over a partial confirmation, or over an
/// analysis carrying a flagged finding nobody has opened.
///
/// Being the LAST human checkpoint before procurement raises the stakes on this file rather than lowering
/// them. Removing one gate and leaving the other blind would be worse than either choice alone.
public class DecisionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly InMemorySdsCorpusReader _corpus = new();
    private readonly HttpClient _client;
    private const string P = "proj-vp-1";

    public DecisionEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
            // MSDS-before-order now asks the SDS corpus whether a validated, indexed sheet exists —
            // not whether a human signed one (design 2026-07-29, D9). The corpus IS the gate's input.
            s.AddSingleton<ISdsCorpusReader>(_corpus);
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
                new TraceRefs(RecordIds.Verdict(P, "cas-zr", id), RecordIds.Dosing(P))),
        ],
        ProposedCode: new ProposedCode(Ratio(id), ["cas-zr", "cas-y"], "the agent's rationale — history, not a signature"));

    private static VerdictDoc Verdict(string cas, string element, string componentId = "bottle") => new()
    {
        Id = RecordIds.Verdict(P, cas, componentId), ProjectId = P, Cas = cas, ComponentId = componentId,
        Element = element, Form = "f",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true, Determination = Determinations.Recommended, DeterminationReason = "ruled",
    };

    /// The full pre-VP record: Decision `done`, the LIVE analysis (candidates + reviewed verdicts), dosing
    /// with one finalized code per component, and the DecisionDoc carrying the agent's proposals. No
    /// regulatory gate is seeded - there is no such document any more (16.4).
    private async Task SeedProposedDecisionAsync(params string[] componentIds)
    {
        if (componentIds.Length == 0) componentIds = ["bottle"];

        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in Stages.All) p.Stages[s].Status = "done";
        p.Stages[Stages.Decision].Status = "done";   // its agent finished; the VP gate is still unsigned
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
        await SeedProposedDecisionAsync();
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
        await SeedProposedDecisionAsync();
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
        await SeedProposedDecisionAsync();
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
        await SeedProposedDecisionAsync();
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
        await SeedProposedDecisionAsync("bottle", "label");
        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("bottle looks right", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("label", await res.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));
        Assert.All((await _store.GetDecisionAsync(P))!.Components, c => Assert.Null(c.ConfirmedCode));
    }

    // PostDetermination_RefusesWhileRegulatoryUnsigned_422 was here. Its subject - "the R.E. must sign
    // before the VP may" - is gone with the regulatory gate (16.4). There is no assertion to rewrite: the
    // precondition no longer exists, deliberately.
    //
    // What the R.E.'s signature actually stood for at THIS door - has a human looked at the flagged
    // findings - is now EvidenceReview.Outstanding, and the very next test is the one that enforces it.

    [Fact]
    public async Task PostDetermination_RefusesWhileAFlaggedFindingIsUnopened_422()
    {
        // Was PostDetermination_RefusesWhenTheRegulatorySignatureNoLongerCoversTheAnalysis_422. The
        // signature it referred to is gone (16.4) - the CHECK is not, and this is the test that proves it
        // did not go with it.
        //
        // A live, FAILING verdict that nobody has opened sits in the analysis. With the regulatory gate
        // deleted, this endpoint is the LAST human checkpoint before procurement, so it is the last place
        // that flagged finding can be caught. It must block, and the blocker must NAME the substance -
        // a blocker the operator cannot locate is a gate they can never arm.
        await SeedProposedDecisionAsync();
        var candidates = (await _store.GetCandidatesAsync(P))!;
        candidates.Substances.Add(new CandidateSubstance("bottle", "Ba", "f", "cas-ba", null, null, false, "A", "s", []));
        await _store.UpsertCandidatesAsync(candidates);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-ba", "bottle"), ProjectId = P, Cas = "cas-ba", ComponentId = "bottle",
            Element = "Ba", Form = "f",
            Dimensions = [new("ElementGate", VerdictStatus.Fail, [new Citation("regulatory", "x", "t")], 0.9, "fails")],
            // EvidenceReviewed = false - flagged, live, and nobody has ever opened it.
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
        await SeedProposedDecisionAsync();
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
        await SeedProposedDecisionAsync();
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
        Assert.Contains("not 'done'", body);                 // and the park a signature answers
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));                       // nothing written
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task PostDetermination_AfterClose_Refuses_ApproveAndRejectAlike(string determination)
    {
        // A CLOSED project: Marker Library written, conclusion filed, procurement Released. A post-close
        // approve would re-stamp history; a post-close REJECT is the "revocation that revokes nothing" —
        // the gate would flip locked while Procurement stays Released. Both are refused, and the signed
        // gate is untouched either way.
        //
        // What identifies "closed" changed with the parks. It used to be the Decision stage reading `done`,
        // because only the close dispatch wrote that. Now the stage reads `done` the moment its AGENT
        // finishes — before any signature — so RELEASED PROCUREMENT is the only honest marker of a close,
        // and VpGate.NotSignableBlocker reads it there.
        await SeedProposedDecisionAsync();
        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed", ("bottle", Ratio("bottle"))));         // the real signature
        var signedAt = (await _store.GetGateAsync(P, GateTypes.Vp))!.ApprovedAt;
        var decision = (await _store.GetDecisionAsync(P))!;
        decision.Procurement.Status = ProcurementStatus.Released;             // the close releases
        await _store.UpsertDecisionAsync(decision);

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            determination == "approved"
                ? Approve("stamp it again", ("bottle", Ratio("bottle")))
                : new { determination = "rejected", reason = "revoke it" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("closed", await res.Content.ReadAsStringAsync());
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
        await SeedProposedDecisionAsync();
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = "pending";
        await _store.UpsertProjectAsync(project);

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.False(g.GetProperty("armable").GetBoolean());
        var blockers = g.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("'pending'") && b.Contains("not 'done'"));
    }

    // ---- who signed the LAST hard gate ----------------------------------------------------------------

    [Fact]
    public async Task PostDetermination_RecordsTheOperatorAsSigner()
    {
        await SeedProposedDecisionAsync();

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed with the VP", ("bottle", Ratio("bottle"))));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var gate = await _store.GetGateAsync(P, GateTypes.Vp);
        Assert.Equal("operator", gate!.ApprovedBy);
    }

    /// A refusal is not a signature. The invariant: a signer is non-null iff a timestamp is, so a locked
    /// gate carries neither.
    [Fact]
    public async Task PostDetermination_WhenRejected_LeavesTheGateUnsigned()
    {
        await SeedProposedDecisionAsync();

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            new { determination = "rejected", reason = "the ratio is too close to project X's" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var gate = await _store.GetGateAsync(P, GateTypes.Vp);
        Assert.Equal("locked", gate!.Status);
        Assert.Null(gate.ApprovedBy);
        Assert.Null(gate.ApprovedAt);
    }

    /// The VP gate releases procurement and writes the Marker Library, and it is now the ONLY record of a
    /// human decision on the whole journey - so it must be able to say who signed. `Json.Options` ignores
    /// nulls, so an anonymous response object would drop the key entirely and a client could not tell "the
    /// record does not say" from "an older API that never sent this".
    [Fact]
    public async Task GetGateVp_ReportsTheSigner_AndSendsAnUnsignedOneAsNull()
    {
        await SeedProposedDecisionAsync();

        var before = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("approvedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("approvedAt").ValueKind);

        await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed with the VP", ("bottle", Ratio("bottle"))));

        var after = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");
        Assert.Equal("operator", after.GetProperty("approvedBy").GetString());
    }

    /// A gate written before this field existed must not be readable as human-signed.
    [Fact]
    public async Task GetGateVp_LeavesAPreExistingSignatureUnattributed()
    {
        await SeedProposedDecisionAsync();
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Vp), ProjectId = P,
            GateType = GateTypes.Vp, Status = "approved",
            ApprovedAt = "2026-01-01T00:00:00.0000000+00:00",
        });

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.Equal("approved", g.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, g.GetProperty("approvedBy").ValueKind);
    }

    // ---- (F1 layer 3): a pending revision blocks the pen --------------------------------------------------

    private static RevisionDoc PendingRevision(string stage, string key = "r1") => new()
    {
        Id = RecordIds.Revision(P, stage, key), ProjectId = P, Stage = stage,
        Target = "the bottle code", Reason = "the ratio is too close to project X's",
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    [Theory]
    [InlineData(Stages.Dosing)]
    [InlineData(Stages.Decision)]
    public async Task PostDetermination_WhileARevisionIsPending_Refuses_ThenSignsAfterItLands(string stage)
    {
        // The revise run is minutes wide (two LLM calls, an embed, a push) and the stage advertises
        // `awaiting-VP` throughout — the (d) park guard cannot see it. The RevisionDoc CAN be seen: it is
        // durable from POST /revise's 202 until applied/failed, so refusing while one is pending closes
        // the whole window including feed lag. The decision may be about to change; the VP must not sign
        // words that are being rewritten.
        await SeedProposedDecisionAsync();
        var revision = PendingRevision(stage);
        await _store.UpsertRevisionAsync(revision);

        var res = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("sign it now", ("bottle", Ratio("bottle"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains(stage, body);                        // the blocker names the pending stage
        Assert.Contains("pending", body);
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));                      // nothing stamped
        Assert.Null(Assert.Single((await _store.GetDecisionAsync(P))!.Components).ConfirmedCode);

        // ...and a REJECT is refused identically: both verbs write a gate record over a moving target.
        var reject = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            new { determination = "rejected", reason = "no" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reject.StatusCode);
        Assert.Null(await _store.GetGateAsync(P, GateTypes.Vp));

        // Once the revision has LANDED (applied — the executor re-parked the stage), the pen is free.
        revision.Status = RevisionStatus.Applied;
        await _store.UpsertRevisionAsync(revision);
        var after = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination",
            Approve("codes reviewed after the revision landed", ("bottle", Ratio("bottle"))));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("approved", (await _store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    [Fact]
    public async Task GetGateVp_WhileARevisionIsPending_IsNotArmable_AndNamesTheBlocker()
    {
        // The read mirrors the POST's refusal (the same rule as ParkBlocker): armable must mean the POST
        // would accept, or the affordance invites a signature over a decision that is being rewritten.
        await SeedProposedDecisionAsync();
        await _store.UpsertRevisionAsync(PendingRevision(Stages.Dosing));

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.False(g.GetProperty("armable").GetBoolean());
        var blockers = g.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("dosing") && b.Contains("pending"));
    }

    [Fact]
    public async Task PostDetermination_ApproveTwice_PreservesApprovedAt()
    {
        await SeedProposedDecisionAsync();
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
        // Before the decision exists the gate is locked, not armable, and the blocker says exactly what is
        // missing. It is the only gate read left - GET /gate/regulatory went with its gate (16.4).
        var p = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(p);

        var g = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/gate/vp");

        Assert.Equal("locked", g.GetProperty("status").GetString());
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("decision has not run", g.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task GetGateVp_ReportsArmable_ThenApproved_AfterTheDetermination()
    {
        await SeedProposedDecisionAsync();

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
        // The read must report the same blocker the POST enforces: with a proposing decision but NO
        // candidates, the POST 422s "no candidates on file" - a read that says `armable: true` over that
        // state is the lying affordance that gets a gate rubber-stamped.
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
        await SeedProposedDecisionAsync();

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
        await SeedProposedDecisionAsync();
        var decision = (await _store.GetDecisionAsync(P))!;
        decision.Components = [.. decision.Components.Select(c => c with
        {
            ConfirmedCode = Ratio(c.ComponentId), ConfirmedBy = "VP R&D", ConfirmedReason = "reviewed",
        })];
        decision.Procurement.Status = ProcurementStatus.Released;
        await _store.UpsertDecisionAsync(decision);
    }

    /// A validated, indexed sheet in the corpus — and nothing else. No signature, because there is no
    /// longer such a thing: the gate's predicate is "a sheet exists", not "a human signed one".
    private void GivenSheetInCorpus(string cas) =>
        _corpus.Sheets.Add(new SdsCorpusSheet(cas, "Acme Chemicals", "product", "2026-05-01",
            "2026-05-02T00:00:00Z"));

    [Fact]
    public async Task PostOrder_BeforeTheVpGate_Is422()
    {
        // Procurement is a state flag (§4) and only the close dispatch releases it — an order before the
        // VP signature is an order for a product nobody approved.
        await SeedProposedDecisionAsync();   // decision exists, procurement still unreleased
        GivenSheetInCorpus("cas-zr");

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("procurement is not released", await res.Content.ReadAsStringAsync());
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    /// D8/D9: the signature is gone, the gate is not. A sheet that was fetched and indexed but that no
    /// human ever countersigned releases the order — because what protects the person opening the drum is
    /// the existence of the safety sheet, not a checkbox somebody ticked next to it.
    [Fact]
    public async Task PostOrder_ReleasesOnAValidatedSheetWithNoSignature()
    {
        await SeedReleasedAsync();
        GivenSheetInCorpus("cas-zr");            // fetched + indexed, never signed

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_WithNoSheetAtAll_Is422()
    {
        // THE hard precondition survives (§4, and D9 of the 2026-07-29 design): MSDS-before-order still
        // gates an individual order. Only its predicate moved — from "signed" to "exists". An empty
        // corpus means nobody has a safety sheet for this substance, so the order must not exist.
        await SeedReleasedAsync();

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("cas-zr", body);
        Assert.Contains("MSDS-before-order", body);
        Assert.Contains("no safety sheet", body);
        // The 422 is actionable for the first time: fetching is now something the operator can do.
        Assert.Contains("fetch", body);
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        // A governance row with linked projects is NOT a sheet — the overlay never satisfied the gate
        // and must not start to now that the signature it used to carry is gone.
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("cas-y"), Cas = "cas-y", Supplier = "Acme Chemicals", Version = "3.1",
            Date = "2026-05-01", LinkedProjects = [P],
        });
        var res2 = await _client.PostAsync($"/projects/{P}/orders/cas-y", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res2.StatusCode);
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_ForACasOutsideTheConfirmedCodes_Is422()
    {
        // You cannot order what the VP did not sign — even with a perfectly good MSDS on file.
        await SeedReleasedAsync();
        GivenSheetInCorpus("cas-ba");   // a real sheet, but in NO confirmed code

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-ba", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("cas-ba", await res.Content.ReadAsStringAsync());
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }

    [Fact]
    public async Task PostOrder_WithASheetOnFile_RecordsTheOrder()
    {
        await SeedReleasedAsync();
        GivenSheetInCorpus("cas-zr");

        var res = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        // idempotent: re-POST is 202 and still ONE entry — an order is a record, not a counter
        var again = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);
    }
}
