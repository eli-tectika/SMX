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
    private readonly InMemoryRunStore _runs = new();
    private readonly HttpClient _client;

    public ProjectsListEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IRecordStore>(_store);
                s.AddSingleton<IRunStore>(_runs);
            })).CreateClient();
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
            Id = RecordIds.Gate("proj-older", GateTypes.Vp), ProjectId = "proj-older",
            GateType = GateTypes.Vp, Status = "approved",
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

        // The gated project reports its signed gate; where the gate is absent the key is an EXPLICIT null —
        // "no gate yet" must be a value the frontend can read, not a missing field it has to infer.
        Assert.Equal("approved", arr[1].GetProperty("gates").GetProperty("vp").GetString());
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("gates").GetProperty("vp").ValueKind);

        // ONE entry. The regulatory key went with its gate (§16.4), and a row that still carried it would
        // hand the frontend a second signature to render that nothing can ever sign.
        Assert.Equal(1, arr[0].GetProperty("gates").EnumerateObject().Count());
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
        foreach (var s in new[] { Stages.Intake, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
            p.Stages[s].Status = "done";
        // Dosing genuinely FAILED (the only way a stage stops now); Decision finished its proposal and is
        // waiting on nothing -- its signature is outstanding, which `needsSigning` reports, not `stopped`.
        p.Stages[Stages.Dosing].Status = StageStatus.NeedsReview;
        p.Stages[Stages.Dosing].Error = "no batch mass for 'bottle'";
        p.Stages[Stages.Decision].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(p);
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
        // `stopped`, not `blocked`: nobody is waited on any more (execution-core 8). The seeded parks
        // are gone, so the only entry left is the stage that genuinely FAILED and needs the operator.
        var stopped = dash.GetProperty("stopped");
        var dosing = Find(stopped, "stage", "dosing");
        Assert.NotNull(dosing);
        Assert.Equal("needs-review", dosing!.Value.GetProperty("status").GetString());
        Assert.Equal("no batch mass for 'bottle'", dosing.Value.GetProperty("detail").GetString());
        Assert.Null(Find(stopped, "stage", "decision"));   // a finished proposal is not a stall

        // needsSigning: EXACTLY ONE entry, always — the VP's is the only signature there is (§16.4). Its
        // armable/blockers come from the REAL predicates (VpGate.Armable + EvidenceReview.Outstanding: a
        // decision proposing a code for every component, no unopened flagged verdict ⇒ armable, no blockers).
        var signing = dash.GetProperty("needsSigning");
        Assert.Equal(1, signing.GetArrayLength());
        Assert.Equal("vp", signing[0].GetProperty("gate").GetString());
        Assert.True(signing[0].GetProperty("armable").GetBoolean());
        Assert.Equal(0, signing[0].GetProperty("blockers").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_VpEntry_IsNotArmable_WhenCandidatesAreAbsent()
    {
        // The dashboard must never advertise a gate the POST would refuse: with a proposing decision but
        // NO candidates on file, POST …/decision/determination 422s "no candidates on file" — so the vp
        // card must say NOT armable, with that same blocker. This is the identical asymmetry Task 13 closed
        // on GET /gate/vp; the dashboard mirrors it.
        var p = Project("proj-dash-nocand", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Decision].Status = "awaiting-VP";
        await _store.UpsertProjectAsync(p);
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
        Assert.Contains(blockers, b => b!.Contains("'pending'") && b.Contains("not 'done'"));
    }

    [Fact]
    public async Task Dashboard_VpEntry_IsNotArmable_WhileARevisionIsPending()
    {
        // F1 layer 3, mirrored the same way ParkBlocker is: the vp card must not invite a signature while
        // a dosing/decision revision is pending — the POST 422s it, so the card says why instead.
        var p = Project("proj-dash-pendrev", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Decision].Status = "awaiting-VP";
        await _store.UpsertProjectAsync(p);
        // Everything ELSE armable, so the pending revision is the ONLY blocker standing.
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision("proj-dash-pendrev"), ProjectId = "proj-dash-pendrev", GeneratedAt = "t",
            Components = [new ComponentDecision("bottle", [],
                new ProposedCode("Zr:Y = 1.00:0.50", ["cas-zr", "cas-y"], "agent rationale"))],
        });
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("proj-dash-pendrev"), ProjectId = "proj-dash-pendrev",
            Substances = [new CandidateSubstance("bottle", "Zr", "f", "cas-zr", null, null, false, "A", "s", [])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict("proj-dash-pendrev", "cas-zr", "bottle"), ProjectId = "proj-dash-pendrev",
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "f",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            EvidenceReviewed = true,
        });
        await _store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = RecordIds.Revision("proj-dash-pendrev", Stages.Decision, "r1"), ProjectId = "proj-dash-pendrev",
            Stage = Stages.Decision, Target = "the pick", Reason = "too close to project X's ratio",
            CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
        });

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-pendrev/dashboard");

        var vp = Find(dash.GetProperty("needsSigning"), "gate", "vp");
        Assert.NotNull(vp);
        Assert.False(vp!.Value.GetProperty("armable").GetBoolean());
        var blockers = vp.Value.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("decision") && b.Contains("pending"));
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

        // An unrecognised status is NOT reported as a stall. The old fallback existed because any unknown
        // awaiting-* was certainly a park; with the park family deleted, `stopped` is an allow-list of the
        // two genuine failure states, and anything else is simply not stopped.
        Assert.Null(Find(dash.GetProperty("stopped"), "stage", "dosing"));
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
        p.Stages[Stages.Dosing].Status = "needs-review";
        p.Stages[Stages.Dosing].Error = "no metal loading on file for cas-zr";
        await _store.UpsertProjectAsync(p);

        var dash = await _client.GetFromJsonAsync<JsonElement>("/projects/proj-dash-err/dashboard");

        var stopped = dash.GetProperty("stopped");
        Assert.Equal(2, stopped.GetArrayLength());
        var discovery = Find(stopped, "stage", "discovery");
        Assert.Equal("failed", discovery!.Value.GetProperty("status").GetString());
        Assert.Equal("model returned unparseable candidates", discovery.Value.GetProperty("detail").GetString());
        var dosing = Find(stopped, "stage", "dosing");
        Assert.Equal("needs-review", dosing!.Value.GetProperty("status").GetString());
        Assert.Equal("no metal loading on file for cas-zr", dosing.Value.GetProperty("detail").GetString());
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
        Assert.Equal(0, dash.GetProperty("stopped").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_404_ForUnknownProject()
    {
        var resp = await _client.GetAsync("/projects/proj-never-created/dashboard");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// The card's live line. `activeRun` must be ABSENT-as-null rather than missing when a project is
    /// idle — the client renders the line only when it is non-null, and a project that is parked or
    /// settled is the common case this must stay quiet for.
    [Fact]
    public async Task GetProjects_ReportsNoActiveRun_WhenNothingIsRunning()
    {
        await _store.UpsertProjectAsync(Project("proj-idle", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00"));

        var list = await _client.GetFromJsonAsync<JsonElement>("/projects");

        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("activeRun").ValueKind);
    }

    /// Regulatory fans out per substance. A card naming one of N children would be picking an
    /// arbitrary one and implying it was the whole job, so the PARENT wins.
    [Fact]
    public async Task GetProjects_PrefersTheParentRun_OverAFannedOutChild()
    {
        var p = Project("proj-busy", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Regulatory].Status = StageStatus.Running;
        await _store.UpsertProjectAsync(p);

        await _runs.UpsertAsync(new RunDoc
        {
            Id = "run|proj-busy|regulatory|1", ProjectId = "proj-busy", Stage = Stages.Regulatory,
            Agent = "regulatory", Outcome = RunOutcome.Running, StartedAt = "2026-07-16T09:00:00.0000000+00:00",
            Steps = [new RunStep { Seq = 1, Kind = RunStepKind.Started, Text = "Screening 4 substances." }],
        });
        await _runs.UpsertAsync(new RunDoc
        {
            Id = "run|proj-busy|regulatory|2", ProjectId = "proj-busy", Stage = Stages.Regulatory,
            Agent = "regulatory", ParentRunId = "run|proj-busy|regulatory|1", Subject = "1314-23-4|bottle",
            Outcome = RunOutcome.Running, StartedAt = "2026-07-16T09:00:05.0000000+00:00",
            Steps = [new RunStep { Seq = 1, Kind = RunStepKind.Started, Text = "Screening 1314-23-4." }],
        });

        var list = await _client.GetFromJsonAsync<JsonElement>("/projects");
        var active = list[0].GetProperty("activeRun");

        Assert.Equal("regulatory", active.GetProperty("stage").GetString());
        Assert.Equal("regulatory", active.GetProperty("agent").GetString());
        Assert.Equal("Screening 4 substances.", active.GetProperty("lastStep").GetString());
    }

    /// `agent: null` means "a deterministic stage, do not imply a model". Json.Options drops null
    /// properties globally, so an absent key here would read as undefined and the card would happily
    /// print nothing where it must print the stage name instead.
    [Fact]
    public async Task GetProjects_CarriesANullAgent_RatherThanOmittingIt()
    {
        var p = Project("proj-det", "Acme", "Bottle", "2026-07-16T09:00:00.0000000+00:00");
        p.Stages[Stages.Matrix].Status = StageStatus.Running;
        await _store.UpsertProjectAsync(p);
        await _runs.UpsertAsync(new RunDoc
        {
            Id = "run|proj-det|matrix|1", ProjectId = "proj-det", Stage = Stages.Matrix,
            Agent = null, Outcome = RunOutcome.Running, StartedAt = "2026-07-16T09:00:00.0000000+00:00",
        });

        var list = await _client.GetFromJsonAsync<JsonElement>("/projects");
        var active = list[0].GetProperty("activeRun");

        Assert.Equal(JsonValueKind.Null, active.GetProperty("agent").ValueKind);
        Assert.Equal(JsonValueKind.Null, active.GetProperty("lastStep").ValueKind);
    }
}
