using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RunTrailTests
{
    /// Fixed, because the domain has no clock (RevisionDoc.CreatedAt rule) and a test that stamped
    /// its own would assert against a value it cannot predict.
    private const string Now = "2026-07-27T10:00:00.0000000+00:00";

    private static (RunTrail Trail, InMemoryRunStore Store, ThreadEventHub Hub) Make()
    {
        var store = new InMemoryRunStore();
        var hub = new ThreadEventHub();
        var run = new RunDoc { Id = "r1", ProjectId = "p1", Stage = Stages.Pool, Agent = "pool", StartedAt = Now };
        return (new RunTrail(run, store, hub), store, hub);
    }

    [Fact]
    public async Task Step_persists_and_publishes()
    {
        var (trail, store, hub) = Make();
        var subscription = hub.Subscribe("p1", Stages.Pool);

        await trail.StepAsync(RunStepKind.ToolCall, "Searched the corpus — 6 hits.", ct: default);

        var run = await store.GetAsync("p1", "r1", default);
        Assert.Single(run!.Steps);
        // ORDER MATTERS, and the lazy open is why: the first frame a subscriber sees is `entry`
        // (the run group appearing), and only then the `step` that goes inside it. A step arriving
        // first would have nothing to attach to on the client.
        Assert.True(subscription.Reader.TryRead(out var opened));
        Assert.Equal("entry", opened!.Event);
        Assert.True(subscription.Reader.TryRead(out var published));
        Assert.Equal("step", published!.Event);
    }

    // D9: telemetry must never be what fails a regulatory screen.
    [Fact]
    public async Task A_store_failure_does_not_throw()
    {
        var run = new RunDoc { Id = "r1", ProjectId = "p1", Stage = Stages.Pool, StartedAt = Now };
        var trail = new RunTrail(run, new ThrowingRunStore(), new ThreadEventHub());
        await trail.StepAsync(RunStepKind.ToolCall, "Searched.", ct: default); // must not throw
        Assert.Single(run.Steps); // the in-memory run still records it
    }

    [Fact]
    public async Task Completing_stamps_outcome_and_end()
    {
        var (trail, store, _) = Make();
        await trail.CompleteAsync(RunOutcome.Failed, "the agent timed out", default);

        var run = await store.GetAsync("p1", "r1", default);
        Assert.Equal(RunOutcome.Failed, run!.Outcome);
        Assert.NotNull(run.EndedAt);
        Assert.Equal("the agent timed out", run.Error);
    }

    /// `Seq` is the SSE resume cursor — a client reconnects with the last id it saw and the server
    /// replays what came after. If two steps shared a seq, or one repeated, the reconnect would either
    /// duplicate a step or skip one, and the operator would read a trail that never happened.
    [Fact]
    public async Task Steps_are_numbered_monotonically_from_one()
    {
        var (trail, store, _) = Make();

        await trail.StepAsync(RunStepKind.Started, "Screening 3 substances.", ct: default);
        await trail.StepAsync(RunStepKind.ToolCall, "Called search_regulatory — 4 result(s).", ct: default);
        await trail.StepAsync(RunStepKind.Outcome, "Done.", ct: default);

        var run = await store.GetAsync("p1", "r1", default);
        Assert.Equal([1, 2, 3], run!.Steps.Select(s => s.Seq));
    }

    /// A stage body can throw before its first step — a null store, an unreadable record — and
    /// ExecuteAsync's catch completes a trail nothing ever opened. Publishing the `run` frame alone
    /// would give a live subscriber a completion for a group it never saw; the failure would simply
    /// not render. Opening first makes an instantly-failing stage VISIBLE, which is the whole point.
    [Fact]
    public async Task Completing_without_a_step_still_opens_the_run_first()
    {
        var (trail, _, hub) = Make();
        var subscription = hub.Subscribe("p1", Stages.Pool);

        await trail.CompleteAsync(RunOutcome.Failed, "the record store was unreachable", default);

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal("entry", first!.Event);
        Assert.True(subscription.Reader.TryRead(out var second));
        Assert.Equal("run", second!.Event);
    }

    private sealed class ThrowingRunStore : IRunStore
    {
        public Task UpsertAsync(RunDoc run, CancellationToken ct = default) => throw new InvalidOperationException("cosmos is down");
        public Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
