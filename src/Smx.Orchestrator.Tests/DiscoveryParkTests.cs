using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DiscoveryParkTests
{
    private static (StageDispatcher, InMemoryRecordStore, FakeAgentRuns) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents);
    }

    private static async Task<ConstraintsDoc> Seed(InMemoryRecordStore store)
    {
        await store.UpsertProjectAsync(
            ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement));
        return new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "PET", "food contact", ["EU"], "brand")],
            ElementPools = [],
        };
    }

    [Fact]
    public async Task Discovery_ParksWithoutCallingTheAgent_WhenThereAreNoElementPools()
    {
        // The park the design promises. Running the agent here cannot succeed: DiscoveryAgent.Validate
        // requires every candidate's element to be in the pool for its component, and with no pools
        // that rejects EVERY candidate. So it burns a real model call to arrive at a message about
        // pools — which is the thing the operator was going to be asked for anyway.
        var (d, store, agents) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        var stage = (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery];
        Assert.Equal("needs-review", stage.Status);
        Assert.Contains("XRF", stage.Error!);
        Assert.Equal(0, agents.DiscoveryCalls);
    }

    [Fact]
    public async Task TheParkDoesNotCountAsAnAttempt()
    {
        // `attempts` is what StageStatusCard renders as "retried Nx". A park is not a try that failed
        // — showing it as one tells the operator the agent has been struggling when it never ran.
        var (d, store, _) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        Assert.Equal(0, (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Attempts);
    }

    [Fact]
    public async Task ThePark_NamesTheScreenTheOperatorHasToGoTo()
    {
        // stage.Error is rendered verbatim to the operator. "Waiting on physics" with no instruction
        // is a dead end: the one thing they need is where to put the file.
        var (d, store, _) = Sut();

        await d.OnRecordChangedAsync(await Seed(store), default);

        Assert.Contains("Background", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Error!);
    }

    [Fact]
    public async Task Discovery_RunsNormally_OnceThePoolsAreConfirmed()
    {
        // The other half: the park must LIFT. Confirming pools upserts the constraints document, the
        // change feed delivers it here again, and this time the agent runs. Without this, the entry
        // surface would record the physics and nothing would ever happen.
        var (d, store, agents) = Sut();
        var constraints = await Seed(store);
        constraints.ElementPools = [new("bottle", "Zr", "Ka", "V", null)];

        await d.OnRecordChangedAsync(constraints, default);

        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Fact]
    public async Task KnownCandidateMode_IsNotParked()
    {
        // Provided candidates bypass the Discovery agent entirely, so they never meet the pool check.
        // Parking them would break the eval harness for a precondition their path does not have.
        var (d, store, _) = Sut();
        var constraints = await Seed(store);
        constraints.ProvidedCandidates =
            [new("bottle", "Ba", "barium sulfate", "7727-43-7", null, null, true, "A", "known",
                 [new Citation("catalog", "ref-catalog/x", "t")])];

        await d.OnRecordChangedAsync(constraints, default);

        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }
}
