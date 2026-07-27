using System.Text.Json;
using Smx.Backend.Api;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The §7.1/§7.2 read-and-stream contract, exercised at the seam rather than over HTTP.
///
/// BuildThreadAsync and ReplayAsync are internal and called directly (Smx.Backend has
/// InternalsVisibleTo on this assembly) because the routes are not wired into Program.cs yet — the
/// supervisor half of §7.3 lands with them. The two functions ARE the contract: the route bodies do
/// nothing but validate the stage and serialize what these return, so testing them through the pipe
/// would only add a WebApplicationFactory to the assertion.
public class ThreadEndpointsTests
{
    private const string P = "p1";

    /// Fixed, because the domain has no clock (the RevisionDoc.CreatedAt rule) and a test that stamped
    /// its own would assert against a value it cannot predict. Full "O" width — a short form here would
    /// be the very misordering ChatMessageDoc.CreatedAt warns about.
    private static string At(int second) =>
        new DateTimeOffset(2026, 7, 27, 10, 0, second, TimeSpan.Zero).ToString("O");

    private static RunDoc Run(string id, string startedAt, string stage = Stages.Discovery) => new()
    {
        Id = id, ProjectId = P, Stage = stage, Agent = stage, Trigger = RunTriggers.Pipeline,
        StartedAt = startedAt,
    };

    private static ChatMessageDoc Message(string id, string text, string createdAt, string status) => new()
    {
        Id = id, ProjectId = P, Stage = Stages.Discovery, Text = text, Status = status, CreatedAt = createdAt,
    };

    /// The entries as the client sees them: through Json.Options, which is where WhenWritingNull and the
    /// polymorphic-by-runtime-type behaviour actually bite. Asserting on the C# objects would let a
    /// serialization-only regression (a dropped key, a base-type-only projection) pass.
    private static List<JsonElement> Wire(List<object> entries) =>
        [.. JsonSerializer.Deserialize<JsonElement[]>(JsonSerializer.Serialize(entries, Json.Options))!];

    private static string Kind(JsonElement e) => e.GetProperty("kind").GetString()!;
    private static int Seq(JsonElement e) => e.GetProperty("seq").GetInt32();
    private static string Text(JsonElement e) => e.GetProperty("text").GetString()!;

    // ---- §7.1 the merged thread ---------------------------------------------------------------------

    [Fact]
    public async Task Thread_merges_runs_and_chat_turns_in_time_order()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await runs.UpsertAsync(Run("e1", At(1)));
        await store.UpsertChatMessageAsync(Message("m1", "why is Ba tier A?", At(2), ChatStatus.Answered));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal(2, entries.Count);
        Assert.Equal("run", Kind(entries[0]));
        Assert.Equal("message", Kind(entries[1]));
        // seq is the client's dedupe key and MUST be dense and ordered.
        Assert.Equal([1, 2], entries.Select(Seq));
    }

    [Fact]
    public async Task Thread_interleaves_a_message_that_predates_a_run()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Message("m1", "first", At(1), ChatStatus.Answered));
        await runs.UpsertAsync(Run("e1", At(2)));
        await store.UpsertChatMessageAsync(Message("m2", "last", At(3), ChatStatus.Answered));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal(["message", "run", "message"], entries.Select(Kind));
        Assert.Equal([1, 2, 3], entries.Select(Seq));
        Assert.Equal("first", Text(entries[0]));
        Assert.Equal("last", Text(entries[2]));
    }

    /// seq is assigned by POSITION, never derived from the clock. Three records on one tick is not exotic
    /// — a pipeline opens several runs in a burst — and a collided seq makes the client drop an entry as
    /// "already held", which is a step the operator silently never sees.
    [Fact]
    public async Task Seq_is_dense_from_one_when_timestamps_collide()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await runs.UpsertAsync(Run("e1", At(1)));
        await runs.UpsertAsync(Run("e2", At(1)));
        await store.UpsertChatMessageAsync(Message("m1", "same tick", At(1), ChatStatus.Answered));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal([1, 2, 3], entries.Select(Seq));
        Assert.Equal(3, entries.Select(Seq).Distinct().Count());
    }

    /// The ChatTurns.InOrder invariant survives the merge. A reply is stamped when the turn ENDS, so a
    /// second operator message posted while the first was still running carries an EARLIER timestamp than
    /// the answer to the first. Sorting the reply on its own CreatedAt files the answer to the Ba question
    /// under the Hf question — a transcript that lies about who said what.
    [Fact]
    public async Task Reply_stays_anchored_to_the_message_it_answers()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Message("m1", "why is Ba tier A?", At(0), ChatStatus.Answered));
        await store.UpsertChatMessageAsync(Message("m2", "also check Hf", At(20), ChatStatus.Pending));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = "r1", ProjectId = P, Stage = Stages.Discovery, MessageId = "m1",
            Text = "because the background is clean at 4.5 keV", CreatedAt = At(30),
        });

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal(
            ["why is Ba tier A?", "because the background is clean at 4.5 keV", "also check Hf"],
            entries.Select(Text));
        Assert.Equal([1, 2, 3], entries.Select(Seq));
    }

    /// A run opened after the reply was WRITTEN still sorts after it, so the anchor trick does not smear
    /// the conversation past the runs it should follow.
    [Fact]
    public async Task An_anchored_reply_still_sorts_before_a_later_run()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Message("m1", "question", At(0), ChatStatus.Answered));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = "r1", ProjectId = P, Stage = Stages.Discovery, MessageId = "m1",
            Text = "answer", CreatedAt = At(30),
        });
        await runs.UpsertAsync(Run("e1", At(40)));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal(["message", "message", "run"], entries.Select(Kind));
    }

    /// A1 has no mailbox, so `pending` is what an unanswered message is stored as. The client's union is
    /// `queued | answered | failed` from day one and must not change under it when A2 lands.
    [Fact]
    public async Task Pending_maps_to_queued_and_the_other_states_pass_through()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Message("m1", "in flight", At(1), ChatStatus.Pending));
        await store.UpsertChatMessageAsync(Message("m2", "landed", At(2), ChatStatus.Answered));
        var failed = Message("m3", "broke", At(3), ChatStatus.Failed);
        failed.Error = "the model returned 429";
        await store.UpsertChatMessageAsync(failed);

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal(
            ["queued", "answered", "failed"],
            entries.Select(e => e.GetProperty("status").GetString()));
        Assert.DoesNotContain("pending", entries.Select(e => e.GetProperty("status").GetString()));
        Assert.Equal("the model returned 429", entries[2].GetProperty("error").GetString());
    }

    /// §7 declares `error: string | null`, not an optional key. Json.Options ignores nulls globally, so
    /// without the explicit [JsonIgnore(Never)] the key would vanish and read as `undefined` on the client.
    [Fact]
    public async Task A_message_carries_an_explicit_null_error()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Message("m1", "fine", At(1), ChatStatus.Answered));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.True(entries[0].TryGetProperty("error", out var error));
        Assert.Equal(JsonValueKind.Null, error.ValueKind);
    }

    /// §7.1's RunSummary names the identifier `runId` and has no `projectId`. RunDoc calls it `id` and
    /// carries the partition key, so serializing the doc raw would put `run.runId === undefined` in front
    /// of a web track that codes against §7 verbatim — silent on both sides.
    [Fact]
    public async Task A_run_entry_uses_the_contracts_field_names()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        var run = Run("e1", At(1));
        run.Append(RunStepKind.ToolCall, "Searched the corpus — 6 hits.", At(2));
        await runs.UpsertAsync(run);

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));
        var summary = entries[0].GetProperty("run");

        Assert.Equal("e1", summary.GetProperty("runId").GetString());
        Assert.False(summary.TryGetProperty("id", out _));
        Assert.False(summary.TryGetProperty("projectId", out _));
        Assert.Equal(Stages.Discovery, summary.GetProperty("stage").GetString());
        Assert.Equal(RunOutcome.Running, summary.GetProperty("outcome").GetString());
        Assert.Equal(RunTriggers.Pipeline, summary.GetProperty("trigger").GetString());
        // `null ⇒ deterministic stage` is a comparison the client makes; the key must be present.
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("parentRunId").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("endedAt").ValueKind);
        var step = summary.GetProperty("steps").EnumerateArray().Single();
        Assert.Equal(1, step.GetProperty("seq").GetInt32());
        Assert.Equal(RunStepKind.ToolCall, step.GetProperty("kind").GetString());
    }

    /// The base-type-serialization trap: a `List<ThreadEntry>` would emit `{seq, at, kind}` and drop every
    /// payload. This is the assertion that catches a "tidy up the return type" refactor.
    [Fact]
    public async Task Entries_serialize_with_their_payload_not_the_base_shape()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await runs.UpsertAsync(Run("e1", At(1)));
        await store.UpsertChatMessageAsync(Message("m1", "hello", At(2), ChatStatus.Answered));

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.True(entries[0].TryGetProperty("run", out _));
        Assert.Equal("operator", entries[1].GetProperty("role").GetString());
        Assert.Equal("hello", Text(entries[1]));
        Assert.Equal(At(1), entries[0].GetProperty("at").GetString());
    }

    [Fact]
    public async Task A_project_with_nothing_yields_an_empty_thread()
    {
        var thread = await ThreadEndpoints.BuildThreadAsync(
            "proj-nothing", Stages.Discovery, new InMemoryRunStore(), new InMemoryRecordStore(), default);

        Assert.Empty(thread);
    }

    /// One thread per (project, stage) — the stage agents do not share a conversation (Law 9) and neither
    /// do their threads. A run or a turn from another stage in this list is another stage's work rendered
    /// as this one's.
    [Fact]
    public async Task The_thread_is_scoped_to_one_stage()
    {
        var runs = new InMemoryRunStore();
        var store = new InMemoryRecordStore();
        await runs.UpsertAsync(Run("e1", At(1)));
        await runs.UpsertAsync(Run("e2", At(2), Stages.Regulatory));
        await store.UpsertChatMessageAsync(new ChatMessageDoc
        {
            Id = "m1", ProjectId = P, Stage = Stages.Regulatory, Text = "elsewhere",
            Status = ChatStatus.Answered, CreatedAt = At(3),
        });

        var entries = Wire(await ThreadEndpoints.BuildThreadAsync(P, Stages.Discovery, runs, store, default));

        Assert.Equal("e1", Assert.Single(entries).GetProperty("run").GetProperty("runId").GetString());
    }

    // ---- §7.2 the replay ----------------------------------------------------------------------------

    private static async Task<InMemoryRunStore> SeededRunAsync(int steps, bool ended)
    {
        var runs = new InMemoryRunStore();
        var run = Run("e1", At(1));
        for (var i = 1; i <= steps; i++) run.Append(RunStepKind.ToolCall, $"step {i}", At(1 + i));
        if (ended)
        {
            run.Outcome = RunOutcome.Done;
            run.EndedAt = At(1 + steps + 1);
        }
        await runs.UpsertAsync(run);
        return runs;
    }

    [Fact]
    public async Task Replay_emits_the_entry_then_every_step_then_the_terminal_frame()
    {
        var runs = await SeededRunAsync(steps: 3, ended: true);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, since: null, runs, default);

        Assert.Equal(["entry", "step", "step", "step", "run"], frames.Select(f => f.Event));
        Assert.Equal(["e1", "e1.s1", "e1.s2", "e1.s3", "e1.r"], frames.Select(f => f.Id));
    }

    [Fact]
    public async Task Replay_omits_the_terminal_frame_for_a_run_still_going()
    {
        var runs = await SeededRunAsync(steps: 2, ended: false);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, since: null, runs, default);

        Assert.Equal(["e1", "e1.s1", "e1.s2"], frames.Select(f => f.Id));
    }

    [Fact]
    public async Task Replay_returns_only_what_follows_the_cursor()
    {
        var runs = await SeededRunAsync(steps: 3, ended: true);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, "e1.s2", runs, default);

        // Everything AT or before the cursor is gone; everything after it is present, in order.
        Assert.Equal(["e1.s3", "e1.r"], frames.Select(f => f.Id));
        Assert.DoesNotContain(frames, f => f.Id == "e1.s2");
    }

    [Fact]
    public async Task A_cursor_on_the_last_frame_replays_nothing()
    {
        var runs = await SeededRunAsync(steps: 2, ended: true);

        Assert.Empty(await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, "e1.r", runs, default));
    }

    /// An unrecognised cursor replays EVERYTHING rather than nothing. A client resuming with an id from a
    /// superseded run would otherwise sit there looking connected and blank. A duplicate frame is
    /// idempotent on the client; a gap is not.
    [Fact]
    public async Task An_unrecognised_cursor_replays_everything()
    {
        var runs = await SeededRunAsync(steps: 2, ended: true);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, "e-gone.s7", runs, default);

        Assert.Equal(["e1", "e1.s1", "e1.s2", "e1.r"], frames.Select(f => f.Id));
    }

    [Fact]
    public async Task A_null_or_blank_cursor_replays_everything()
    {
        var runs = await SeededRunAsync(steps: 1, ended: false);

        Assert.Equal(2, (await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, null, runs, default)).Count);
        Assert.Equal(2, (await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, "", runs, default)).Count);
    }

    [Fact]
    public async Task Replaying_a_project_with_no_runs_yields_no_frames()
    {
        Assert.Empty(await ThreadEndpoints.ReplayAsync(
            "proj-nothing", Stages.Discovery, null, new InMemoryRunStore(), default));
    }

    /// Replay and the live hub are ONE cursor space. If a replayed id does not match the id the client
    /// already holds for the same event, a reconnect duplicates the trail instead of deduping it — so this
    /// pins the replayed ids against what RunTrail actually publishes, rather than against a copy of the
    /// format string.
    [Fact]
    public async Task Replayed_frame_ids_match_what_RunTrail_publishes_live()
    {
        var runs = new InMemoryRunStore();
        var hub = new ThreadEventHub();
        var subscription = hub.Subscribe(P, Stages.Discovery);
        var trail = new RunTrail(Run("e1", At(1)), runs, hub);

        await trail.StepAsync(RunStepKind.ToolCall, "Searched the corpus — 6 hits.");
        await trail.CompleteAsync(RunOutcome.Done, null, default);

        var live = new List<ThreadFrame>();
        while (subscription.Reader.TryRead(out var frame)) live.Add(frame);
        var replayed = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, null, runs, default);

        Assert.Equal(live.Select(f => f.Id), replayed.Select(f => f.Id));
        Assert.Equal(live.Select(f => f.Event), replayed.Select(f => f.Event));
    }

    /// The `step` and `run` frames are handed straight to the client's reconciler, which keys on runId.
    [Fact]
    public async Task Step_and_terminal_frames_carry_the_runId_and_outcome()
    {
        var runs = await SeededRunAsync(steps: 1, ended: true);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, null, runs, default);
        var step = JsonSerializer.SerializeToElement(frames[1].Data, Json.Options);
        var terminal = JsonSerializer.SerializeToElement(frames[2].Data, Json.Options);

        Assert.Equal("e1", step.GetProperty("runId").GetString());
        Assert.Equal("step 1", step.GetProperty("step").GetProperty("text").GetString());
        Assert.Equal("e1", terminal.GetProperty("runId").GetString());
        Assert.Equal(RunOutcome.Done, terminal.GetProperty("outcome").GetString());
        Assert.Equal(At(3), terminal.GetProperty("endedAt").GetString());
    }

    /// The replayed `entry` frame is a ThreadEntry, exactly as §7.2 says — and seq 0, matching
    /// RunTrail.OpenAsync, because a run's position in the merged thread is not known when it opens.
    [Fact]
    public async Task The_replayed_entry_frame_is_a_thread_entry()
    {
        var runs = await SeededRunAsync(steps: 0, ended: false);

        var frames = await ThreadEndpoints.ReplayAsync(P, Stages.Discovery, null, runs, default);
        var entry = JsonSerializer.SerializeToElement(frames[0].Data, Json.Options);

        Assert.Equal("run", entry.GetProperty("kind").GetString());
        Assert.Equal(0, entry.GetProperty("seq").GetInt32());
        Assert.Equal(At(1), entry.GetProperty("at").GetString());
        Assert.Equal("e1", entry.GetProperty("run").GetProperty("runId").GetString());
    }
}
