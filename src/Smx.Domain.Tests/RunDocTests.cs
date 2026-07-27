using System.Text.Json;
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
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool, StartedAt = "2026-07-27T10:00:00.0000000Z" };
        run.Append(RunStepKind.Started, "Started.", "2026-07-27T10:00:00.0000000Z");
        run.Append(RunStepKind.ToolCall, "Searched.", "2026-07-27T10:00:01.0000000Z");
        Assert.Equal(new[] { 1, 2 }, run.Steps.Select(s => s.Seq));
    }

    // The caller-supplied timestamp must land on the step unchanged — Append must never read the clock
    // itself (the domain has no clock; see StartedAt's doc comment).
    [Fact]
    public void Append_stamps_the_caller_supplied_timestamp_onto_the_step()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool, StartedAt = "2026-07-27T10:00:00.0000000Z" };
        var step = run.Append(RunStepKind.Started, "Started.", "2026-07-27T10:00:05.0000000Z");
        Assert.Equal("2026-07-27T10:00:05.0000000Z", step.At);
    }

    [Fact]
    public void A_run_starts_running_with_no_end()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool, StartedAt = "2026-07-27T10:00:00.0000000Z" };
        Assert.Equal(RunOutcome.Running, run.Outcome);
        Assert.Null(run.EndedAt);
    }

    // Pinned wire contract (design doc §7): a parallel frontend track codes against these exact property
    // names. A silent rename here would break them with no local failure — this test is that failure.
    [Fact]
    public void RunDoc_round_trips_through_the_pinned_wire_shape()
    {
        var run = new RunDoc
        {
            Id = "run|proj-1|discovery|3",
            ProjectId = "proj-1",
            Stage = Stages.Discovery,
            Agent = "discovery-agent",
            Subject = "7440-38-2|comp-1",
            ParentRunId = "run|proj-1|regulatory|1",
            Trigger = RunTriggers.OperatorRetry,
            StartedAt = "2026-07-27T10:00:00.0000000Z",
            EndedAt = "2026-07-27T10:00:05.0000000Z",
            Outcome = RunOutcome.Done,
            Error = "boom",
        };
        run.Append(
            RunStepKind.ToolCall,
            "Searched.",
            "2026-07-27T10:00:01.0000000Z",
            new RunStepDetail
            {
                Tool = "search_web",
                Query = "arsenic trioxide CAS 1327-53-3",
                ResultCount = 4,
                RecordId = "proj-1|verdict|1327-53-3|comp-1",
                Attempt = 1,
                Of = 3,
            });

        var json = JsonSerializer.Serialize(run, Smx.Domain.Json.Options);

        foreach (var name in new[]
        {
            "id", "projectId", "stage", "agent", "subject", "parentRunId", "trigger", "startedAt",
            "endedAt", "outcome", "error", "steps",
        })
        {
            Assert.Contains($"\"{name}\"", json);
        }
        foreach (var name in new[] { "seq", "at", "kind", "text", "detail" })
        {
            Assert.Contains($"\"{name}\"", json);
        }
        foreach (var name in new[] { "tool", "query", "resultCount", "recordId", "attempt", "of" })
        {
            Assert.Contains($"\"{name}\"", json);
        }

        var roundTripped = JsonSerializer.Deserialize<RunDoc>(json, Smx.Domain.Json.Options)!;
        Assert.Equal(run.Id, roundTripped.Id);
        Assert.Equal(run.ProjectId, roundTripped.ProjectId);
        Assert.Equal(run.Stage, roundTripped.Stage);
        Assert.Equal(run.Agent, roundTripped.Agent);
        Assert.Equal(run.Subject, roundTripped.Subject);
        Assert.Equal(run.ParentRunId, roundTripped.ParentRunId);
        Assert.Equal(run.Trigger, roundTripped.Trigger);
        Assert.Equal(run.StartedAt, roundTripped.StartedAt);
        Assert.Equal(run.EndedAt, roundTripped.EndedAt);
        Assert.Equal(run.Outcome, roundTripped.Outcome);
        Assert.Equal(run.Error, roundTripped.Error);
        Assert.Single(roundTripped.Steps);
        var step = roundTripped.Steps[0];
        var original = run.Steps[0];
        Assert.Equal(original.Seq, step.Seq);
        Assert.Equal(original.At, step.At);
        Assert.Equal(original.Kind, step.Kind);
        Assert.Equal(original.Text, step.Text);
        Assert.NotNull(step.Detail);
        Assert.Equal(original.Detail!.Tool, step.Detail!.Tool);
        Assert.Equal(original.Detail!.Query, step.Detail!.Query);
        Assert.Equal(original.Detail!.ResultCount, step.Detail!.ResultCount);
        Assert.Equal(original.Detail!.RecordId, step.Detail!.RecordId);
        Assert.Equal(original.Detail!.Attempt, step.Detail!.Attempt);
        Assert.Equal(original.Detail!.Of, step.Detail!.Of);
    }
}
