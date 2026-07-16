using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// GET /projects — the estate list. Newest-first, each row carrying the stage spine and both gate
/// statuses, because the landing page's "Needs signing" card cannot be computed from anything less.
public class ProjectsListEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ProjectsListEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private static ProjectDoc Project(string id, string client, string product, string createdAt)
    {
        var doc = ProjectDoc.Create(id, client, product, JsonSerializer.SerializeToElement(new { }));
        doc.CreatedAt = createdAt;
        return doc;
    }

    [Fact]
    public async Task GetProjects_ListsNewestFirst_WithStagesAndGates()
    {
        // Seeded OLDER first: [newer, older] on the wire can only come from the ORDER BY, not from
        // insertion order echoing back.
        await _store.UpsertProjectAsync(Project("proj-older", "Acme", "Shampoo bottle", "2026-07-15T10:00:00.0000000+00:00"));
        await _store.UpsertProjectAsync(Project("proj-newer", "Globex", "Serum label", "2026-07-16T09:00:00.0000000+00:00"));
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("proj-older", GateTypes.Regulatory), ProjectId = "proj-older",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-07-15T12:00:00.0000000+00:00",
        });

        var resp = await _client.GetAsync("/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("proj-newer", arr[0].GetProperty("projectId").GetString());
        Assert.Equal("proj-older", arr[1].GetProperty("projectId").GetString());
        Assert.Equal("Globex", arr[0].GetProperty("client").GetString());
        Assert.Equal("Serum label", arr[0].GetProperty("product").GetString());
        Assert.Equal("2026-07-16T09:00:00.0000000+00:00", arr[0].GetProperty("createdAt").GetString());

        // The stage spine rides along, statuses included — straight off the ProjectDoc.
        Assert.Equal("pending", arr[0].GetProperty("stages").GetProperty("intake").GetProperty("status").GetString());
        Assert.Equal("pending", arr[1].GetProperty("stages").GetProperty("decision").GetProperty("status").GetString());

        // The gated project reports its signed gate; everywhere a gate is absent the key is an EXPLICIT
        // null — "no gate yet" must be a value the frontend can read, not a missing field it has to infer.
        Assert.Equal("approved", arr[1].GetProperty("gates").GetProperty("regulatory").GetString());
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("gates").GetProperty("regulatory").ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("gates").GetProperty("vp").ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[1].GetProperty("gates").GetProperty("vp").ValueKind);
    }

    [Fact]
    public async Task GetProjects_EmptyStore_ReturnsEmptyArray()
    {
        // Cold start is an empty estate, not an error: [] — never 404.
        var resp = await _client.GetAsync("/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(0, arr.GetArrayLength());
    }

    /// The route returns EVERY project, and 120 is deliberately past the 50 the store pages at — a page size
    /// is a round-trip unit, and the moment it becomes a limit this test fails.
    ///
    /// A cap here would not look like a bug. The dashboard has no paging and no search, so the list is the
    /// only route to a project and a dropped project is an unreachable one; and because the "Needs signing"
    /// card is computed from these rows, a truncated list retires a gate that is genuinely awaiting the VP
    /// from the one surface that exists to raise it. Parked projects are precisely the ones that age out of
    /// a newest-first cut, which is the same asynchronous pause/resume the whole system is built around.
    [Fact]
    public async Task GetProjects_ReturnsEveryProject_PastThePageSize()
    {
        for (var i = 0; i < 120; i++)
            await _store.UpsertProjectAsync(Project($"proj-{i:D3}", "Acme", "Bottle",
                $"2026-07-16T{i / 60:D2}:{i % 60:D2}:00.0000000+00:00"));

        var arr = await _client.GetFromJsonAsync<JsonElement>("/projects");

        Assert.Equal(120, arr.GetArrayLength());
    }

    /// The projection contract. The payload is the entire intake body and no card reads a byte of it, so
    /// shipping one per project would be pure weight; without this the route starts doing exactly that the
    /// day someone returns the whole doc.
    [Fact]
    public async Task GetProjects_DoesNotShipThePayload()
    {
        await _store.UpsertProjectAsync(Project("proj-1", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00"));

        var item = (await _client.GetFromJsonAsync<JsonElement>("/projects")).EnumerateArray().Single();

        Assert.False(item.TryGetProperty("payload", out _));
        Assert.Equal("Acme", item.GetProperty("client").GetString());
    }

    /// The record container is ONE bucket of discriminated types partitioned by project. Without the `type`
    /// filter this route would hand the dashboard every matrix, verdict and gate in the system as though
    /// each were a project.
    [Fact]
    public async Task GetProjects_ListsOnlyProjectDocs()
    {
        await _store.UpsertProjectAsync(Project("proj-1", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00"));
        await _store.UpsertMatrixAsync(new MatrixDoc
        {
            Id = RecordIds.Matrix("proj-1"), ProjectId = "proj-1", Columns = ["bottle"], GeneratedAt = "t",
        });

        var arr = await _client.GetFromJsonAsync<JsonElement>("/projects");

        Assert.Equal(1, arr.GetArrayLength());
    }

    // ---- GET /projects/{id}/dashboard (Task 12) ---------------------------------------------------------
    // §7: "what's blocked and on whom, what's ready to continue, what needs signing" — a pure projection
    // over the ProjectDoc + the two GateDocs. Every fact already lives in StageState.Status/.Error and the
    // gate records; the dashboard computes, it never stores.

    private static JsonElement? Find(JsonElement array, string prop, string value)
    {
        foreach (var el in array.EnumerateArray())
            if (el.GetProperty(prop).GetString() == value) return el;
        return null;
    }

    [Fact]
    public async Task Dashboard_NamesTheBlocker()
    {
        // The whole point of `on` is naming the RIGHT owner: the operator chasing themselves for the
        // physicist's number is exactly the UX failure the spec calls out. awaiting-physics is PHYSICS'
        // ball, awaiting-VP is the VP's — and the park message (StageState.Error) rides as the detail.
        var p = Project("proj-dash", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        foreach (var s in new[] { Stages.Intake, Stages.Discovery, Stages.Regulatory, Stages.Matrix, Stages.Cost })
            p.Stages[s].Status = "done";
        p.Stages[Stages.Dosing].Status = "awaiting-physics";
        p.Stages[Stages.Dosing].Error = "no batch mass for 'bottle'";
        p.Stages[Stages.Decision].Status = "awaiting-VP";
        await _store.UpsertProjectAsync(p);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("proj-dash", GateTypes.Regulatory), ProjectId = "proj-dash",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision("proj-dash"), ProjectId = "proj-dash", GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [],
                new ProposedCode("Zr:Y = 1.00:0.50", ["cas-zr", "cas-y"], "agent rationale"))],
        });
        // The LIVE analysis the regulatory signature covers — armable must mean the POST would accept,
        // and the POST re-checks coverage against candidates + verdicts, not just VpGate.Armable.
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("proj-dash"), ProjectId = "proj-dash",
            Substances = [new CandidateSubstance("bottle", "Zr", "f", "cas-zr", null, null, false, "A", "s", [])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict("proj-dash", "cas-zr", "bottle"), ProjectId = "proj-dash",
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "f",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            EvidenceReviewed = true,
        });

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash/dashboard");

        Assert.Equal("proj-dash", dash.GetProperty("projectId").GetString());
        var blocked = dash.GetProperty("blocked");
        Assert.Equal(2, blocked.GetArrayLength());
        var dosing = Find(blocked, "stage", "dosing");
        Assert.NotNull(dosing);
        Assert.Equal("physics", dosing!.Value.GetProperty("on").GetString());
        Assert.Equal("no batch mass for 'bottle'", dosing.Value.GetProperty("detail").GetString());
        var decision = Find(blocked, "stage", "decision");
        Assert.NotNull(decision);
        Assert.Equal("VP R&D", decision!.Value.GetProperty("on").GetString());

        // needsSigning: the regulatory gate is APPROVED — signed gates don't need signing — so only the
        // vp entry appears, with armable/blockers from the REAL predicate (VpGate.Armable: approved
        // regulatory gate + a decision proposing a code for every component ⇒ armable, no blockers).
        var signing = dash.GetProperty("needsSigning");
        Assert.Equal(1, signing.GetArrayLength());
        Assert.Equal("vp", signing[0].GetProperty("gate").GetString());
        Assert.True(signing[0].GetProperty("armable").GetBoolean());
        Assert.Equal(0, signing[0].GetProperty("blockers").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_VpEntry_IsNotArmable_WhenCandidatesAreAbsent()
    {
        // The dashboard must never advertise a gate the POST would refuse: with an approved regulatory
        // gate and a proposing decision but NO candidates on file, POST …/decision/determination 422s
        // "no candidates on file" — so the vp card must say NOT armable, with that same blocker. This is
        // the identical asymmetry Task 13 closed on GET /gate/vp; the dashboard mirrors it.
        var p = Project("proj-dash-nocand", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Decision].Status = "awaiting-VP";
        await _store.UpsertProjectAsync(p);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("proj-dash-nocand", GateTypes.Regulatory), ProjectId = "proj-dash-nocand",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision("proj-dash-nocand"), ProjectId = "proj-dash-nocand", GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [],
                new ProposedCode("Zr:Y = 1.00:0.50", ["cas-zr", "cas-y"], "agent rationale"))],
        });

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-nocand/dashboard");

        var vp = Find(dash.GetProperty("needsSigning"), "gate", "vp");
        Assert.NotNull(vp);
        Assert.False(vp!.Value.GetProperty("armable").GetBoolean());
        Assert.Contains("no candidates on file", vp.Value.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task Dashboard_VpEntry_IsNotArmable_WhileTheDecisionStageIsNotParked()
    {
        // Task 15(d): the POST refuses any determination unless the Decision stage is parked
        // `awaiting-VP` — a Dosing revision resets it to `pending` while the STALE DecisionDoc is still
        // on file. The dashboard mirrors the gate read's coverage logic (the Tasks 12-14 review), so it
        // must surface the same park blocker instead of advertising the gate the POST 422s.
        var p = Project("proj-dash-repick", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Decision].Status = "pending";   // mid-re-pick
        await _store.UpsertProjectAsync(p);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("proj-dash-repick", GateTypes.Regulatory), ProjectId = "proj-dash-repick",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "2026-07-16T00:00:00.0000000+00:00",
        });
        // Everything ELSE armable — the stale DecisionDoc proposes, the live analysis is covered — so the
        // park blocker is the ONLY thing standing, and a dropped mirror would flip armable to true.
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision("proj-dash-repick"), ProjectId = "proj-dash-repick", GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [],
                new ProposedCode("Zr:Y = 1.00:0.50", ["cas-zr", "cas-y"], "the stale pick"))],
        });
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("proj-dash-repick"), ProjectId = "proj-dash-repick",
            Substances = [new CandidateSubstance("bottle", "Zr", "f", "cas-zr", null, null, false, "A", "s", [])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict("proj-dash-repick", "cas-zr", "bottle"), ProjectId = "proj-dash-repick",
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "f",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            EvidenceReviewed = true,
        });

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-repick/dashboard");

        var vp = Find(dash.GetProperty("needsSigning"), "gate", "vp");
        Assert.NotNull(vp);
        Assert.False(vp!.Value.GetProperty("armable").GetBoolean());
        var blockers = vp.Value.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("'pending'") && b.Contains("not 'awaiting-VP'"));
    }

    [Fact]
    public async Task Dashboard_AnUnmappedAwaitingStatus_SurfacesOnTheOperator_NeverVanishes()
    {
        // A future awaiting-* the mapping doesn't know yet must still SURFACE — a park that silently
        // drops off the blocked list is a stall nobody notices (§11). The operator is the honest
        // fallback owner: they triage every park anyway.
        var p = Project("proj-dash-newpark", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Dosing].Status = "awaiting-somethingnew";
        p.Stages[Stages.Dosing].Error = "parked on a state this build has never heard of";
        await _store.UpsertProjectAsync(p);

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-newpark/dashboard");

        var blocked = dash.GetProperty("blocked");
        var dosing = Find(blocked, "stage", "dosing");
        Assert.NotNull(dosing);
        Assert.Equal("operator", dosing!.Value.GetProperty("on").GetString());
        Assert.Equal("parked on a state this build has never heard of", dosing.Value.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Dashboard_NeedsReviewAndFailed_BlockOnTheOperator_WithTheStageError()
    {
        // needs-review/failed → the operator's ball, with StageState.Error as the detail — an error nobody
        // surfaces is a stall nobody notices (§11).
        var p = Project("proj-dash-err", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Intake].Status = "done";
        p.Stages[Stages.Discovery].Status = "failed";
        p.Stages[Stages.Discovery].Error = "model returned unparseable candidates";
        p.Stages[Stages.Cost].Status = "needs-review";
        p.Stages[Stages.Cost].Error = "supplier price unparseable for cas-zr";
        await _store.UpsertProjectAsync(p);

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-err/dashboard");

        var blocked = dash.GetProperty("blocked");
        Assert.Equal(2, blocked.GetArrayLength());
        var discovery = Find(blocked, "stage", "discovery");
        Assert.Equal("operator", discovery!.Value.GetProperty("on").GetString());
        Assert.Equal("model returned unparseable candidates", discovery.Value.GetProperty("detail").GetString());
        var cost = Find(blocked, "stage", "cost");
        Assert.Equal("operator", cost!.Value.GetProperty("on").GetString());
        Assert.Equal("supplier price unparseable for cas-zr", cost.Value.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Dashboard_ReadyStages()
    {
        // Stages.All IS the pipeline order: a pending stage whose upstream neighbour is done is the next
        // action. Cost is pending too but its upstream (dosing) is only pending — not ready yet.
        var p = Project("proj-dash-ready", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        foreach (var s in new[] { Stages.Intake, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
            p.Stages[s].Status = "done";
        await _store.UpsertProjectAsync(p);

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-ready/dashboard");

        var ready = dash.GetProperty("readyToContinue");
        Assert.Equal(1, ready.GetArrayLength());
        Assert.Equal("dosing", ready[0].GetString());
        Assert.Equal(0, dash.GetProperty("blocked").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_404_ForUnknownProject()
    {
        var resp = await _client.GetAsync("/projects/proj-never-created/dashboard");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
