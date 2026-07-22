using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class StageDispatcherTests
{
    private static (StageDispatcher, InMemoryRecordStore, FakeAgentRuns) Sut(int parallelism = 2)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, parallelism), store, agents);
    }

    private static async Task<ProjectDoc> Seed(InMemoryRecordStore store)
    {
        var doc = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    [Fact]
    public async Task ProjectCreated_RunsIntake_WritesConstraints_MarksStageDone()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        Assert.Equal(1, agents.IntakeCalls);
        Assert.NotNull(await store.GetConstraintsAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task ConstraintsWritten_RunsDiscovery_WritesCandidates()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.NotNull(await store.GetCandidatesAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    /// Constraints with NO element pool and NO provided candidates — the need-only path. Intake produces just
    /// the need (with the substrate's physical state); the pool agent proposes the pool.
    private static void NeedOnly(FakeAgentRuns agents) =>
        agents.Intake = p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", null, "solid")],
            ElementPools = [],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    // The need-only journey: the operator enters only the need, so Intake writes a ConstraintsDoc with no
    // element pool. That must run the POOL agent (not Discovery), and the pool's own write is what then drives
    // Background (pass-through) and Discovery — over the proposed pool, mapped onto the constraints in memory.
    [Fact]
    public async Task NeedOnly_RunsPoolAgent_ThenBackgroundPassthrough_ThenDiscoveryOverTheProposedPool()
    {
        var (d, store, agents) = Sut();
        NeedOnly(agents);
        ConstraintsDoc? handedToDiscovery = null;
        var run = agents.Discovery;
        agents.Discovery = (pr, c, r) => { handedToDiscovery = c; return run(pr, c, r); };

        // Intake → need-only ConstraintsDoc.
        await d.OnRecordChangedAsync(await Seed(store), default);
        var constraints = (await store.GetConstraintsAsync("p1"))!;

        // The ConstraintsDoc write runs the pool agent — NOT Discovery yet.
        await d.OnRecordChangedAsync(constraints, default);
        Assert.Equal(1, agents.PoolCalls);
        Assert.Equal(0, agents.DiscoveryCalls);
        var pool = await store.GetPoolAsync("p1");
        Assert.NotNull(pool);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Pool].Status);

        // The PoolDoc write drives Background (pass-through) and Discovery over the proposed pool.
        await d.OnRecordChangedAsync(pool!, default);
        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.NotNull(await store.GetCandidatesAsync("p1"));
        var p = (await store.GetProjectAsync("p1"))!;
        Assert.Equal("done", p.Stages[Stages.Background].Status);
        Assert.Equal("done", p.Stages[Stages.Discovery].Status);

        // Discovery was handed the proposed pool (mapped onto the in-memory constraints), not an empty one.
        Assert.NotNull(handedToDiscovery);
        Assert.Contains(handedToDiscovery!.ElementPools, e => e.Component == "bottle" && e.Element == "Zr");
        // ...and the PERSISTED constraints stay frozen: the map is in-memory only.
        Assert.Empty((await store.GetConstraintsAsync("p1"))!.ElementPools);
    }

    // At-least-once feed: a redelivered need-only ConstraintsDoc must not re-run the pool agent once a pool exists.
    [Fact]
    public async Task NeedOnly_RedeliveredConstraints_DoesNotRerunPoolAgent()
    {
        var (d, store, agents) = Sut();
        NeedOnly(agents);
        await d.OnRecordChangedAsync(await Seed(store), default);
        var constraints = (await store.GetConstraintsAsync("p1"))!;
        await d.OnRecordChangedAsync(constraints, default);
        await d.OnRecordChangedAsync(constraints, default); // redelivery
        Assert.Equal(1, agents.PoolCalls);
    }

    // Discovery is the only stage that can reach the public internet, and its web-search tool is built from
    // the ProjectDoc's client/product/project-id — the terms it must refuse to send. The dispatcher is what
    // hands them over: a ConstraintsDoc carries neither name, so a Discovery run dispatched without the
    // project is a run whose external search has nothing to protect.
    [Fact]
    public async Task Discovery_IsHandedTheProject_TheOnlyRecordCarryingTheTermsTheWebMustNotSee()
    {
        var (d, store, agents) = Sut();
        ProjectDoc? handedToDiscovery = null;
        var run = agents.Discovery;
        agents.Discovery = (p, c, r) => { handedToDiscovery = p; return run(p, c, r); };

        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);

        Assert.NotNull(handedToDiscovery);
        Assert.Equal("p1", handedToDiscovery!.ProjectId);
        Assert.Equal("Acme", handedToDiscovery.Client);
        Assert.Equal("P", handedToDiscovery.Product);
    }

    /// Constraints carrying operator-supplied candidates, ready to hand to the dispatcher. `cas` is a
    /// parameter because the CAS is the one thing this door has to check.
    private static void ProvideCandidates(FakeAgentRuns agents, string cas) =>
        agents.Intake = p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ProvidedCandidates = [new("bottle", "Zr", "oxide", cas, null, null, true, "A", "provided",
                [new Citation("catalog", "x", "t")])],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    [Fact]
    public async Task ConstraintsWithProvidedCandidates_BypassesDiscoveryAgent()
    {
        var (d, store, agents) = Sut();
        ProvideCandidates(agents, "1314-23-4");                 // zirconium dioxide — a REAL, valid CAS
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        Assert.Equal(0, agents.DiscoveryCalls);                 // bypassed
        Assert.Single((await store.GetCandidatesAsync("p1"))!.Substances);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    /// The known-candidate door is the ONE path into the record that no agent validates. DiscoveryAgent.Validate
    /// check-digits every CAS a model proposes — but ProvidedCandidates skips Discovery entirely and lands in
    /// the CandidatesDoc verbatim, so that rail never runs. From there the CAS flows into the regulatory screen,
    /// into dosing (against the wrong molecular weight) and into procurement, carrying exactly the authority of
    /// a candidate an agent had cited.
    ///
    /// A CAS check digit makes a transposed digit PROVABLY wrong, so there is no reason to let one through.
    /// The rest of Validate's rails are deliberately NOT applied here: these candidates come from the operator
    /// or an eval fixture, not from a model, so a hallucinated tier is not the risk. A mistyped CAS is.
    [Fact]
    public async Task ProvidedCandidateWithABadCheckDigit_IsRefused_NotWrittenAsCandidates()
    {
        var (d, store, agents) = Sut();
        ProvideCandidates(agents, "1314-23-5");                 // one digit off: the check digit is 4, not 5
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);

        Assert.Null(await store.GetCandidatesAsync("p1"));      // it must NOT become the candidate set
        var stage = (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery];
        Assert.Equal("needs-review", stage.Status);             // parked for the operator, the file's convention
        Assert.Contains("1314-23-5", stage.Error);              // and the record says which one and why
        Assert.Contains("check digit", stage.Error);
    }

    /// Non-numeric junk is the same defect wearing different clothes (the old fixture here said "cas-zr").
    [Fact]
    public async Task ProvidedCandidateWithAMalformedCas_IsRefused()
    {
        var (d, store, agents) = Sut();
        ProvideCandidates(agents, "cas-zr");
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);

        Assert.Null(await store.GetCandidatesAsync("p1"));
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Fact]
    public async Task CandidatesWritten_FansOutRegulatory_ThenAssemblesMatrix()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);              // intake
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default); // discovery
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);  // regulatory fan-out
        Assert.Single(await store.GetVerdictsAsync("p1"));
        var last = (await store.GetVerdictsAsync("p1"))[0];
        await d.OnRecordChangedAsync(last, default);                            // verdict → assembly
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("awaiting-RE", proj!.Stages[Stages.Regulatory].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
    }

    [Fact]
    public async Task RegulatoryFanOut_SkipsCellsThatAlreadyHaveVerdicts()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var candidates = await store.GetCandidatesAsync("p1");
        await d.OnRecordChangedAsync(candidates!, default);
        var callsAfterFirst = agents.RegulatoryCalls;
        await d.OnRecordChangedAsync(candidates!, default);                     // redelivery
        Assert.Equal(callsAfterFirst, agents.RegulatoryCalls);
    }

    [Fact]
    public async Task DiscoveryNeedsReview_MarksStage_DoesNotCascade()
    {
        var (d, store, agents) = Sut();
        agents.Discovery = (_, _, _) => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>.NeedsReview("no catalog hits"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("needs-review", proj!.Stages[Stages.Discovery].Status);
        Assert.Null(await store.GetCandidatesAsync("p1"));
    }

    [Fact]
    public async Task RegulatoryNeedsReview_WritesPlaceholderVerdict_MatrixStillAssembles()
    {
        var (d, store, agents) = Sut();
        agents.Regulatory = (c, cand, _) => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.Single(verdicts);
        Assert.Equal(VerdictStatus.NeedsReview, verdicts[0].Overall);
        await d.OnRecordChangedAsync(verdicts[0], default);
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedRegulatoryGate_MovesRegulatoryStageToDone()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task LockedRegulatoryGate_DoesNotAdvanceStage()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "locked" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task GateApprovedBeforeVerdictsComplete_StageGoesDoneOnAssembly()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        // Gate approved early (before the regulatory fan-out assembles the matrix).
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default); // fan-out → assemble
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedNonRegulatoryGate_DoesNotAdvanceRegulatoryStage()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        // A future VP gate flows through the same OnGateAsync — it must NOT advance Regulatory.
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", "vp"), ProjectId = "p1",
            GateType = "vp", Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", "vp"))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedRegulatoryGate_DoesNotOverwriteFailedStage()
    {
        var (d, store, _) = Sut();
        var proj = await Seed(store);
        proj.Stages[Stages.Regulatory].Status = "failed";
        await store.UpsertProjectAsync(proj);

        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("failed", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedRegulatoryGate_RedeliveredAfterDone_StaysDone()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        var gate = (await store.GetGateAsync("p1", GateTypes.Regulatory))!;
        await d.OnRecordChangedAsync(gate, default);
        await d.OnRecordChangedAsync(gate, default); // at-least-once re-delivery must be a no-op
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task IntakeThrow_MarksStageFailed_WithErrorDetail()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("failed", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("foundry 500", proj.Stages[Stages.Intake].Error);
    }
}
