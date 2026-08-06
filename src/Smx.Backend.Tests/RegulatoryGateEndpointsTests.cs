using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Backend.Api;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RegulatoryGateEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RegulatoryGateEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private async Task SeedVerdict(string pid, string cas, VerdictStatus overall)
    {
        var proj = ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(pid, cas, "bottle"), ProjectId = pid, Cas = cas, ComponentId = "bottle",
            Element = "Zr", Form = "neodec",
            Dimensions = [new("ElementGate", overall, [new Citation("r", "x", "t")], 0.9, "r")],
        });
        // Register this (cas, bottle) cell as a non-C candidate so the verdict set is COMPLETE
        // (MatrixAssembler.IsComplete counts non-C cells and finds this verdict).
        var candidates = await _store.GetCandidatesAsync(pid)
            ?? new CandidatesDoc { Id = RecordIds.Candidates(pid), ProjectId = pid };
        candidates.Substances.Add(new CandidateSubstance(
            "bottle", "Zr", "neodec", cas, null, null, false, "A", "seed", []));
        await _store.UpsertCandidatesAsync(candidates);
    }

    [Fact]
    public async Task Review_MarksVerdictEvidenceReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "cas1", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.EvidenceReviewed);
    }

    [Fact]
    public async Task Review_Returns404_ForUnknownVerdict()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "nope", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Determination_Recommend_SetsFieldsAndReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Conditional);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = "supplier COA confirms compliance" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("recommended", v!.Determination);
        Assert.Equal("supplier COA confirms compliance", v.DeterminationReason);
        Assert.True(v.EvidenceReviewed);
    }

    [Fact]
    public async Task Determination_RecommendWithoutReason_Returns422()
    {
        // Every determination — including recommending a flagged item — must carry a reason.
        await SeedVerdict("p1", "cas1", VerdictStatus.Conditional);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.Determination);
    }

    [Fact]
    public async Task Determination_RejectWithoutReason_Returns422()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "rejected", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.Determination);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("approved")]      // a plausible synonym is still not the word
    [InlineData("Recommended")]   // CASE. CompliantSet's filter is ordinal, so a capital R would never be
    [InlineData(" recommended ")] // WHITESPACE. Same: an untrimmed string matches nothing downstream.
    public async Task Determination_ThatIsNotExactlyOneOfTheTwoConstants_Returns422(string determination)
    {
        // This endpoint is the ONLY writer of VerdictDoc.Determination, and CompliantSet reads that field
        // with an ordinal ==. So this 422 is what guarantees the string CompliantSet sees is always one of
        // the two constants. A future "helpful" OrdinalIgnoreCase or .Trim() here would let a determination
        // be persisted that the compliant-set filter cannot recognise — the operator signs, and the
        // substance silently never gets dosed. Fails closed, but it fails silently, which is worse to debug.
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination, reason = "a reason" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Determination_RejectWithReason_PersistsReasonAndReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "rejected", reason = "EU Cosmetics Annex III" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("rejected", v!.Determination);
        Assert.Equal("EU Cosmetics Annex III", v.DeterminationReason);
        Assert.True(v.EvidenceReviewed);
    }

    [Fact]
    public async Task Determination_Returns404_ForUnknownVerdict()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "nope", componentId = "bottle", determination = "recommended", reason = "n/a" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Approve_Returns422WithBlockers_WhenFlaggedItemUnreviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail); // flagged + unreviewed
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("cas1", await resp.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task Approve_WritesApprovedGate_WhenArmable()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass); // clean → armable without review
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var g = await _store.GetGateAsync("p1", GateTypes.Regulatory);
        Assert.NotNull(g);
        Assert.Equal("approved", g!.Status);
        Assert.False(string.IsNullOrEmpty(g.ApprovedAt));
    }

    [Fact]
    public async Task Approve_Returns422_WhenVerdictSetIncomplete()
    {
        // A non-C candidate with NO verdict → MatrixAssembler.IsComplete is false.
        var proj = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = { new CandidateSubstance("bottle", "Zr", "neodec", "cas1", null, null, false, "A", "seed", []) },
        });
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("incomplete", await resp.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task GetGate_ReportsLockedWithBlockers_BeforeApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("locked", g.GetProperty("status").GetString());
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("cas1", g.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task GetGate_ReportsApproved_AfterApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("approved", g.GetProperty("status").GetString());
        Assert.True(g.GetProperty("armable").GetBoolean());
    }

    [Fact]
    public async Task Approve_Twice_PreservesOriginalApprovedAt()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var first = (await _store.GetGateAsync("p1", GateTypes.Regulatory))!.ApprovedAt;
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var second = (await _store.GetGateAsync("p1", GateTypes.Regulatory))!.ApprovedAt;
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Approve_IsNotBlockedByAnOrphanVerdict_ForACandidateARevisionRetieredToC()
    {
        // The bricked-journey regression. A revise ("exclude Ba, it overlaps the Ti K-beta line") re-tiers a
        // candidate to C but leaves its old unreviewed Fail verdict in the store. Ba is now in no matrix row
        // and no matrix cell, so the operator has no affordance to open and review it — yet the gate used to
        // name it as a blocker and return 422 FOREVER. Arm on the live analysis, not on everything ever
        // written.
        await SeedVerdict("p1", "cas-ba", VerdictStatus.Fail);   // flagged + unreviewed, tier A
        var candidates = (await _store.GetCandidatesAsync("p1"))!;
        candidates.Substances[0] = candidates.Substances[0] with { Tier = "C" };   // the revise
        await _store.UpsertCandidatesAsync(candidates);

        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("approved", (await _store.GetGateAsync("p1", GateTypes.Regulatory))!.Status);
    }

    [Fact]
    public async Task Approve_StillBlocks_WhenALiveFlaggedVerdictIsUnreviewed_BesideAnOrphan()
    {
        // ...and the narrowing above must not weaken the gate. Zr is still screened, still flagged, still
        // unreviewed: 422, naming Zr and only Zr.
        await SeedVerdict("p1", "cas-ba", VerdictStatus.Fail);
        await SeedVerdict("p1", "cas-zr", VerdictStatus.Fail);
        var candidates = (await _store.GetCandidatesAsync("p1"))!;
        candidates.Substances[0] = candidates.Substances[0] with { Tier = "C" };   // Ba re-tiered → orphan
        await _store.UpsertCandidatesAsync(candidates);

        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("cas-zr", body);
        Assert.DoesNotContain("cas-ba", body);
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task GetGate_ReportsNotArmableWithIncompleteBlocker_WhenVerdictSetIncomplete()
    {
        // A candidate with no verdict → incomplete set. Build candidates directly (SeedVerdict would add a verdict).
        var proj = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new CandidateSubstance("bottle", "Zr", "neodec", "cas1", null, null, false, "A", "seed", [])],
        });
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("incomplete", g.GetProperty("blockers").ToString());
    }

    /// THE SIGNATURE HAS TO MOVE SOMETHING. Dosing SKIPS behind an unsigned regulatory gate, and nothing
    /// watches the record any more — so before this endpoint learned to re-enter the pipeline, the R.E.'s
    /// determination changed a gate document and the project sat exactly where it was, parked under a
    /// signature the operator had already given.
    ///
    /// Recording the signature and running agents stay separate acts: OnGateAsync is the one-record stamp,
    /// and the SUPERVISOR is what re-enters. This proves the pair of them, through the real endpoint.
    [Fact]
    public async Task Approve_StampsTheStage_AndRe_entersThePipelineSoDosingAdvances()
    {
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        foreach (var stage in new[] { Stages.Intake, Stages.Discovery, Stages.Matrix })
            project.Stages[stage].Status = "done";
        // Regulatory lands `done` off its own run now (execution-core §8) — the signature is recorded on the
        // GateDoc, not in the stage's status.
        project.Stages[Stages.Regulatory].Status = "done";
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "K\u03b1", "V", null)],
        });
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new("bottle", "Zr", "neodec", "cas1", null, null, false, "A", "seed", [])],
        });
        await store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "cas1", "bottle"), ProjectId = "p1", Cas = "cas1",
            ComponentId = "bottle", Element = "Zr", Form = "neodec", EvidenceReviewed = true,
            Determination = Determinations.Recommended, DeterminationReason = "cleared",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("r", "x", "t")], 0.9, "r")],
        });

        var runs = new InMemoryRunStore();
        var runner = new PipelineRunner(store, runs, new FakeAgentRuns(), new ThreadEventHub(),
            new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
                new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance), 2);
        var supervisor = new PipelineSupervisor(store, runs, runner, NullLogger<PipelineSupervisor>.Instance);
        using var app = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(store);
            s.AddSingleton<IRunStore>(runs);
            s.AddSingleton(runner);
            s.AddSingleton(supervisor);
        }));

        var res = await app.CreateClient().PostAsync("/projects/p1/regulatory/approve", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await supervisor.Completion("p1");

        // The signature is recorded on the GATE, which is now the only place it lives. The stage status is
        // about whether the stage's own agent ran, and approving does not change that.
        Assert.Equal("approved", (await store.GetGateAsync("p1", GateTypes.Regulatory))!.Status);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        // The re-entry, which is what this test is really for: POST /approve stamps the record and then hands
        // off to the supervisor rather than blocking in Foundry on the caller's thread. Dosing RAN — it left
        // `pending`, which is the proof it executed. It stops at needs-review because this project has NO
        // device and NO measured background, so there is no floor and no generic limit to fall back to, and
        // its only substance is dropped by name rather than dosed against nothing.
        var dosing = (await store.GetProjectAsync("p1"))!.Stages[Stages.Dosing];
        Assert.Equal(StageStatus.NeedsReview, dosing.Status);
        Assert.Contains("floor", dosing.Error);
    }

    [Fact]
    public async Task Approve_RecordsTheOperatorAsSigner()
    {
        await SeedVerdict("pApprovedBy", "cas1", VerdictStatus.Pass);
        var resp = await _client.PostAsJsonAsync("/projects/pApprovedBy/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var gate = await _store.GetGateAsync("pApprovedBy", GateTypes.Regulatory);
        Assert.Equal("operator", gate!.ApprovedBy);
    }

    /// The API must SAY who signed. Without this property the screen cannot tell a human
    /// determination from REGULATORY_AUTO_APPROVE, and renders both as an approved gate.
    [Fact]
    public async Task GetGate_ReportsTheSigner()
    {
        await SeedVerdict("pSigner", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/pSigner/regulatory/approve", new { });

        var json = await _client.GetFromJsonAsync<JsonElement>("/projects/pSigner/gate/regulatory");
        Assert.Equal("operator", json.GetProperty("approvedBy").GetString());
    }

    /// A gate written before this field existed must NOT be readable as human-signed. Null stays
    /// null all the way to the client, which renders it as unknown provenance.
    [Fact]
    public async Task GetGate_LeavesAPreExistingSignatureUnattributed()
    {
        await SeedVerdict("pLegacy", "cas1", VerdictStatus.Pass);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("pLegacy", GateTypes.Regulatory), ProjectId = "pLegacy",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-01-01T00:00:00.0000000+00:00",
        });

        var json = await _client.GetFromJsonAsync<JsonElement>("/projects/pLegacy/gate/regulatory");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("approvedBy").ValueKind);
    }

    /// Once REGULATORY_AUTO_APPROVE merges, this is the operator catching up on a gate the pipeline
    /// signed itself: they review the evidence for real and press Sign. The machine's signature must
    /// not survive that — both the signer AND the timestamp have to move, or the record keeps
    /// asserting the machine's review covers the current analysis.
    [Fact]
    public async Task Approve_WhenTheStandingSignatureIsTheMachines_ReplacesItWithTheOperatorsAndAFreshTimestamp()
    {
        await SeedVerdict("pAutoApprove", "cas1", VerdictStatus.Pass);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("pAutoApprove", GateTypes.Regulatory), ProjectId = "pAutoApprove",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-01-01T00:00:00.0000000+00:00", ApprovedBy = "auto-approve",
        });

        var resp = await _client.PostAsJsonAsync("/projects/pAutoApprove/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var gate = await _store.GetGateAsync("pAutoApprove", GateTypes.Regulatory);
        Assert.Equal("operator", gate!.ApprovedBy);
        Assert.NotEqual("2026-01-01T00:00:00.0000000+00:00", gate.ApprovedAt);
    }

    /// A gate approved before ApprovedBy existed carries no signer at all — not "operator", just
    /// unknown. An operator pressing Sign over it today is a real signature happening NOW, not a
    /// retroactive claim on whoever (or whatever) approved it at the old timestamp.
    [Fact]
    public async Task Approve_WhenTheStandingSignatureIsUnattributed_AttributesItToTheOperatorWithAFreshTimestamp()
    {
        await SeedVerdict("pLegacyApprove", "cas1", VerdictStatus.Pass);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("pLegacyApprove", GateTypes.Regulatory), ProjectId = "pLegacyApprove",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-01-01T00:00:00.0000000+00:00",
        });

        var resp = await _client.PostAsJsonAsync("/projects/pLegacyApprove/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var gate = await _store.GetGateAsync("pLegacyApprove", GateTypes.Regulatory);
        Assert.Equal("operator", gate!.ApprovedBy);
        Assert.NotEqual("2026-01-01T00:00:00.0000000+00:00", gate.ApprovedAt);
    }
}
