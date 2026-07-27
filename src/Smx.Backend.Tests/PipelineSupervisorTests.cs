using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Backend.Agents;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The supervisor: one pipeline task per project, the registry every control endpoint resolves against,
/// and the boot-time resume.
///
/// The resume is the reason this type is a hosted service at all. Without it a process that dies mid-run
/// leaves the project at `running` with nothing to restart it — the checkpoint-and-lose failure the
/// change-feed model had, and the one thing that model was supposed to prevent.
public class PipelineSupervisorTests
{
    private sealed record Sut(
        PipelineSupervisor Supervisor,
        InMemoryRecordStore Store,
        InMemoryRunStore Runs,
        FakeAgentRuns Agents,
        PipelineRunner Runner);

    private static Sut Build()
    {
        var store = new InMemoryRecordStore();
        var runs = new InMemoryRunStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        var runner = new PipelineRunner(store, runs, agents, new ThreadEventHub(), conclusions, 2);
        return new Sut(
            new PipelineSupervisor(store, runs, runner, NullLogger<PipelineSupervisor>.Instance),
            store, runs, agents, runner);
    }

    private static async Task<ProjectDoc> SeedAsync(InMemoryRecordStore store, string projectId = "p1")
    {
        var doc = ProjectDoc.Create(projectId, "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    /// Holds the pipeline inside the intake agent until the test releases it, so "a pipeline is live" is a
    /// state the test controls rather than one it races.
    private static TaskCompletionSource BlockIntake(FakeAgentRuns agents)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = agents.Intake;
        agents.Intake = async p => { await gate.Task; return await inner(p); };
        return gate;
    }

    // ---- one pipeline per project -------------------------------------------------------------------

    [Fact]
    public async Task Starting_twice_refuses_the_second()
    {
        var sut = Build();
        await SeedAsync(sut.Store);
        var gate = BlockIntake(sut.Agents);

        Assert.True(sut.Supervisor.TryStart("p1"));
        Assert.False(sut.Supervisor.TryStart("p1"));

        gate.SetResult();
        await sut.Supervisor.Completion("p1");
    }

    [Fact]
    public async Task A_finished_pipeline_frees_the_project()
    {
        var sut = Build();
        await SeedAsync(sut.Store);

        Assert.True(sut.Supervisor.TryStart("p1"));
        await sut.Supervisor.Completion("p1");

        Assert.False(sut.Supervisor.IsRunning("p1"));
        Assert.True(sut.Supervisor.TryStart("p1"));
        await sut.Supervisor.Completion("p1");
    }

    /// Two projects are two pipelines. The registry is keyed on the project, not a global lock.
    [Fact]
    public async Task Another_project_starts_while_one_is_live()
    {
        var sut = Build();
        await SeedAsync(sut.Store, "p1");
        await SeedAsync(sut.Store, "p2");
        var gate = BlockIntake(sut.Agents);

        Assert.True(sut.Supervisor.TryStart("p1"));
        Assert.True(sut.Supervisor.TryStart("p2"));

        gate.SetResult();
        await Task.WhenAll(sut.Supervisor.Completion("p1"), sut.Supervisor.Completion("p2"));
    }

    /// A pipeline that throws must not wedge the project shut. The wrapper logs and releases the slot —
    /// otherwise one bad run would make the project permanently unstartable, with a 409 as the only symptom.
    ///
    /// The throw comes from the RUN STORE, which is deliberate: the runner catches everything a stage BODY
    /// can throw and stamps it `failed`, so the only way out of RunAsync is a failure of the trail itself.
    [Fact]
    public async Task A_pipeline_that_throws_still_releases_the_project()
    {
        var store = new InMemoryRecordStore();
        await SeedAsync(store);
        var runner = new PipelineRunner(store, new ThrowingRunStore(), new FakeAgentRuns(),
            new ThreadEventHub(),
            new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
                new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance), 2);
        var supervisor = new PipelineSupervisor(
            store, new ThrowingRunStore(), runner, NullLogger<PipelineSupervisor>.Instance);

        Assert.True(supervisor.TryStart("p1"));
        await supervisor.Completion("p1");

        Assert.False(supervisor.IsRunning("p1"));
        Assert.True(supervisor.TryStart("p1"));
        await supervisor.Completion("p1");
    }

    private sealed class ThrowingRunStore : IRunStore
    {
        public Task UpsertAsync(RunDoc run, CancellationToken ct = default) =>
            throw new InvalidOperationException("cosmos is down");
        public Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct = default) =>
            throw new InvalidOperationException("cosmos is down");
        public Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct = default) =>
            throw new InvalidOperationException("cosmos is down");
    }

    // ---- cancel -------------------------------------------------------------------------------------

    /// The supervisor is the registry the cancel endpoint resolves against; the runner owns the CTS. A run
    /// nobody is holding is not cancellable — and saying so is what turns into the endpoint's 409.
    [Fact]
    public void Cancelling_a_run_nobody_holds_is_false()
    {
        var sut = Build();
        Assert.False(sut.Supervisor.CancelRun(RunIds.Run("p1", Stages.Discovery, 1)));
    }

    [Fact]
    public async Task Cancelling_a_live_run_reaches_the_runner()
    {
        var sut = Build();
        await SeedAsync(sut.Store);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Agents.Intake = async p =>
        {
            reached.SetResult();
            await release.Task;
            return AgentRunResult<ConstraintsDoc>.NeedsReview("never gets here");
        };

        Assert.True(sut.Supervisor.TryStart("p1"));
        await reached.Task;

        // The intake run is open and live by the time the agent is running — ExecuteAsync registers the CTS
        // before the trail writes the run's first step.
        Assert.True(sut.Supervisor.CancelRun(RunIds.Run("p1", Stages.Intake, 1)));
        release.SetResult();
        await sut.Supervisor.Completion("p1");
    }

    // ---- resume -------------------------------------------------------------------------------------

    /// Previously: a project whose process died sat at `running` forever, with nothing left on the feed to
    /// redeliver it. Now the orphaned run is stamped — FIRST, so the trail shows the gap rather than hiding
    /// it — and the pipeline re-enters.
    [Fact]
    public async Task Resume_stamps_an_orphaned_run_interrupted_and_re_enters()
    {
        var sut = Build();
        var project = await SeedAsync(sut.Store);
        project.Stages[Stages.Discovery].Status = "running";
        await sut.Store.UpsertProjectAsync(project);
        await sut.Runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Discovery, 1), ProjectId = "p1",
            Stage = Stages.Discovery, Outcome = RunOutcome.Running,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        await sut.Supervisor.ResumeAllAsync(default);
        await sut.Supervisor.Completion("p1");

        var orphan = await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Discovery, 1));
        Assert.Equal(RunOutcome.Interrupted, orphan!.Outcome);
        Assert.NotNull(orphan.EndedAt);
        Assert.NotNull(orphan.Error);
        // Re-entered, not merely stamped: `running` counts as not-run, so Discovery ran again and landed.
        Assert.Equal("done", (await sut.Store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
        Assert.NotNull(await sut.Store.GetCandidatesAsync("p1"));
    }

    /// A project with nothing in `running` is not resumed. Re-entering every project on every boot would
    /// re-run stages nobody asked for, and would restart a project the operator has not started yet.
    [Fact]
    public async Task Resume_leaves_alone_a_project_with_no_running_stage()
    {
        var sut = Build();
        var project = await SeedAsync(sut.Store);
        project.Stages[Stages.Intake].Status = StageStatus.AwaitingConfirmation;
        await sut.Store.UpsertProjectAsync(project);

        await sut.Supervisor.ResumeAllAsync(default);

        Assert.False(sut.Supervisor.IsRunning("p1"));
        Assert.Null(await sut.Store.GetConstraintsAsync("p1"));
    }

    /// A terminal run is history and must not be rewritten. Stamping one `interrupted` at boot would turn a
    /// completed analysis into a gap the operator has to re-investigate.
    [Fact]
    public async Task Resume_does_not_touch_a_run_that_already_ended()
    {
        var sut = Build();
        var project = await SeedAsync(sut.Store);
        project.Stages[Stages.Discovery].Status = "running";
        await sut.Store.UpsertProjectAsync(project);
        await sut.Runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Intake, 1), ProjectId = "p1", Stage = Stages.Intake,
            Outcome = RunOutcome.Done, StartedAt = "2026-07-27T10:00:00.0000000+00:00",
            EndedAt = "2026-07-27T10:00:05.0000000+00:00",
        });

        await sut.Supervisor.ResumeAllAsync(default);
        await sut.Supervisor.Completion("p1");

        var done = await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1));
        Assert.Equal(RunOutcome.Done, done!.Outcome);
        Assert.Null(done.Error);
    }

    /// THE RACE. Kestrel is listening before this hosted service's ExecuteAsync runs, so a start can land
    /// mid-resume. A run this process is holding RIGHT NOW is not orphaned, and stamping it `interrupted`
    /// would put a lie in the trail of a run that is still going.
    [Fact]
    public async Task Resume_does_not_stamp_a_run_this_process_is_still_holding()
    {
        var sut = Build();
        var project = await SeedAsync(sut.Store);
        project.Stages[Stages.Discovery].Status = "running";
        await sut.Store.UpsertProjectAsync(project);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = sut.Agents.Intake;
        sut.Agents.Intake = async p => { reached.SetResult(); await release.Task; return await inner(p); };

        // The request beat the boot resume to it.
        Assert.True(sut.Supervisor.TryStart("p1"));
        await reached.Task;
        await sut.Supervisor.ResumeAllAsync(default);

        var live = await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1));
        Assert.Equal(RunOutcome.Running, live!.Outcome);
        release.SetResult();
        await sut.Supervisor.Completion("p1");
        Assert.Equal(RunOutcome.Done, (await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1)))!.Outcome);
    }

    /// The same race one level down. The supervisor's registry is keyed on the PROJECT, so it cannot see a
    /// run held by anything that drives the runner directly — and a run the runner is still executing is not
    /// orphaned, whoever started it. `PipelineRunner.IsLive` is the per-run check that closes that gap.
    [Fact]
    public async Task Resume_does_not_stamp_a_run_the_runner_itself_still_holds()
    {
        var sut = Build();
        var project = await SeedAsync(sut.Store);
        project.Stages[Stages.Discovery].Status = "running";
        await sut.Store.UpsertProjectAsync(project);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = sut.Agents.Intake;
        sut.Agents.Intake = async p => { reached.TrySetResult(); await release.Task; return await inner(p); };

        // Driven straight, NOT through the supervisor — so the project is absent from its registry.
        var direct = sut.Runner.RunAsync("p1", default);
        await reached.Task;

        await sut.Supervisor.ResumeAllAsync(default);

        var live = await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1));
        Assert.Equal(RunOutcome.Running, live!.Outcome);

        release.SetResult();
        await direct;
        await sut.Supervisor.Completion("p1");
    }

    /// Resume walks EVERY project, not the first one it finds.
    [Fact]
    public async Task Resume_re_enters_every_stalled_project()
    {
        var sut = Build();
        foreach (var id in new[] { "p1", "p2" })
        {
            var project = await SeedAsync(sut.Store, id);
            project.Stages[Stages.Discovery].Status = "running";
            await sut.Store.UpsertProjectAsync(project);
        }

        await sut.Supervisor.ResumeAllAsync(default);
        await Task.WhenAll(sut.Supervisor.Completion("p1"), sut.Supervisor.Completion("p2"));

        Assert.NotNull(await sut.Store.GetCandidatesAsync("p1"));
        Assert.NotNull(await sut.Store.GetCandidatesAsync("p2"));
    }

    // ---- shutdown -----------------------------------------------------------------------------------

    /// A host shutdown must leave the stage RESUMABLE — the runner rethrows without stamping, deliberately,
    /// so the next boot's resume stamps the run `interrupted` and re-enters. An operator cancel is the only
    /// thing that stamps `cancelled`; the difference is the host token, and this is what proves the
    /// supervisor actually threads it into every run it starts.
    [Fact]
    public async Task Host_shutdown_cancels_the_live_pipeline_and_leaves_the_stage_running()
    {
        var sut = Build();
        await SeedAsync(sut.Store);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Agents.Intake = async _ =>
        {
            // LastIntakeToken is captured by the fake BEFORE this delegate runs, so this IS the token the
            // runner handed the stage — the one the supervisor must have linked to its own shutdown. If it
            // were not, this delay would never return and the test would hang rather than pass quietly.
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, sut.Agents.LastIntakeToken);
            return AgentRunResult<ConstraintsDoc>.NeedsReview("unreachable");
        };

        await sut.Supervisor.StartAsync(default);
        Assert.True(sut.Supervisor.TryStart("p1"));
        await reached.Task;
        await sut.Supervisor.StopAsync(default);

        // Not `cancelled`, not `failed`: the stage is exactly where it was, which is what makes the next
        // boot's resume able to stamp the run `interrupted` and pick the stage up.
        Assert.Equal("running", (await sut.Store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
        Assert.Equal(RunOutcome.Running,
            (await sut.Runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1)))!.Outcome);
        Assert.False(sut.Supervisor.IsRunning("p1"));
    }
}
