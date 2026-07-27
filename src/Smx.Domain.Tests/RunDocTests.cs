using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RunDocTests
{
    // The id is a Cosmos item id and must survive being concatenated into further ids and URLs.
    // Cosmos rejects '/', '\', '?' and '#' outright — a 400 no in-memory test store can produce.
    [Fact]
    public void RunId_contains_no_character_cosmos_rejects()
    {
        var id = RunIds.Run("proj-1", Stages.Discovery, 3);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('\\', id);
        Assert.DoesNotContain('?', id);
        Assert.DoesNotContain('#', id);
    }

    [Fact]
    public void RunId_is_stable_and_ordinal_scoped()
    {
        Assert.Equal(RunIds.Run("proj-1", Stages.Pool, 1), RunIds.Run("proj-1", Stages.Pool, 1));
        Assert.NotEqual(RunIds.Run("proj-1", Stages.Pool, 1), RunIds.Run("proj-1", Stages.Pool, 2));
    }

    // Steps carry their own monotonic seq because it is the client's reconciliation key: a replayed
    // frame after a reconnect must be recognisable as one already held.
    [Fact]
    public void Append_assigns_monotonic_seq_from_one()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool };
        run.Append(RunStepKind.Started, "Started.");
        run.Append(RunStepKind.ToolCall, "Searched.");
        Assert.Equal(new[] { 1, 2 }, run.Steps.Select(s => s.Seq));
    }

    [Fact]
    public void A_run_starts_running_with_no_end()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool };
        Assert.Equal(RunOutcome.Running, run.Outcome);
        Assert.Null(run.EndedAt);
    }
}
