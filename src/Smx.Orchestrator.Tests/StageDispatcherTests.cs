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
    public async Task ProjectChange_WithIntakeAlreadyDone_DoesNotRerunIntake()
    {
        var (d, store, agents) = Sut();
        var doc = await Seed(store);
        await d.OnRecordChangedAsync(doc, default);
        await d.OnRecordChangedAsync((await store.GetProjectAsync("p1"))!, default); // change-feed redelivery
        Assert.Equal(1, agents.IntakeCalls);
    }

    [Fact]
    public async Task ConstraintsWritten_FansOutScreening_PerCell_ThenAssemblesMatrix()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);              // intake
        var constraints = await store.GetConstraintsAsync("p1");
        await d.OnRecordChangedAsync(constraints!, default);                    // screening fan-out
        Assert.Single(await store.GetVerdictsAsync("p1"));                      // 1 substance × 1 component
        var last = (await store.GetVerdictsAsync("p1"))[0];
        await d.OnRecordChangedAsync(last, default);                            // verdict arrival → assembly
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("done", proj!.Stages[Stages.Screening].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
    }

    [Fact]
    public async Task ScreeningFanOut_SkipsCellsThatAlreadyHaveVerdicts()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        var constraints = await store.GetConstraintsAsync("p1");
        await d.OnRecordChangedAsync(constraints!, default);
        var callsAfterFirst = agents.ScreenCalls;
        await d.OnRecordChangedAsync(constraints!, default); // redelivery
        Assert.Equal(callsAfterFirst, agents.ScreenCalls);
    }

    [Fact]
    public async Task IntakeNeedsReview_MarksStage_DoesNotCascade()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.NeedsReview("uncited scope"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("needs-review", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("uncited scope", proj.Stages[Stages.Intake].Error);
        Assert.Null(await store.GetConstraintsAsync("p1"));
    }

    [Fact]
    public async Task AgentThrow_MarksStageFailed_WithErrorDetail()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("failed", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("foundry 500", proj.Stages[Stages.Intake].Error);
        Assert.Equal(1, proj.Stages[Stages.Intake].Attempts);
    }

    [Fact]
    public async Task NeedsReviewVerdict_StillCountsTowardCompletion_ScreeningStageEndsNeedsReview()
    {
        var (d, store, agents) = Sut();
        agents.Screen = (c, s, comp) => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.Single(verdicts);                                   // placeholder NeedsReview verdict written
        Assert.Equal(VerdictStatus.NeedsReview, verdicts[0].Overall);
        await d.OnRecordChangedAsync(verdicts[0], default);
        Assert.NotNull(await store.GetMatrixAsync("p1"));          // matrix still assembles (cells say NeedsReview)
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Screening].Status);
    }
}
