using System.Text.Json;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class StageDispatcherTests
{
    private static (StageDispatcher, InMemoryRecordStore, FakeAgentRuns) Sut(int parallelism = 2)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        return (new StageDispatcher(store, agents, parallelism), store, agents);
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

    [Fact]
    public async Task ConstraintsWithProvidedCandidates_BypassesDiscoveryAgent()
    {
        var (d, store, agents) = Sut();
        agents.Intake = p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ProvidedCandidates = [new("bottle", "Zr", "neodec", "cas-zr", null, null, true, "A", "provided",
                [new Citation("catalog", "x", "t")])],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        Assert.Equal(0, agents.DiscoveryCalls);                 // bypassed
        Assert.Single((await store.GetCandidatesAsync("p1"))!.Substances);
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
        agents.Discovery = (_, _) => Task.FromResult(
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
