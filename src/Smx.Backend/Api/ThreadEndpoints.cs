using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// §7.1's `RunSummary`. Deliberately NOT <see cref="RunDoc"/> serialized raw, which is what it looks like
/// it could be: the pinned contract names the identifier `runId` where the doc calls it `id`, and carries
/// no `projectId` at all. Serializing the doc would put `{ id, projectId, … }` on the wire, and the web
/// track — which codes against §7 verbatim — would read `run.runId` as `undefined` on every entry. That
/// failure is silent on both sides, which is exactly why the translation is explicit and lives here.
///
/// Every nullable field is `[JsonIgnore(Never)]` because <see cref="Json.Options"/> sets
/// `DefaultIgnoreCondition = WhenWritingNull` globally. §7 declares these as `T | null`, not `T?` — an
/// omitted key reads as `undefined`, and `agent === null` ("deterministic stage, no model involved") is a
/// comparison the client actually makes. Present-and-null is the contract; absent is not.
public sealed record RunSummary
{
    public required string RunId { get; init; }
    public required string Stage { get; init; }
    /// null ⇒ a deterministic stage. The UI must not imply a model was involved.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Agent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Subject { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ParentRunId { get; init; }
    public required string Trigger { get; init; }
    public required string StartedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? EndedAt { get; init; }
    public required string Outcome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Error { get; init; }
    public IReadOnlyList<RunStep> Steps { get; init; } = [];

    public static RunSummary From(RunDoc run) => new()
    {
        RunId = run.Id,
        Stage = run.Stage,
        Agent = run.Agent,
        Subject = run.Subject,
        ParentRunId = run.ParentRunId,
        Trigger = run.Trigger,
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        Outcome = run.Outcome,
        Error = run.Error,
        Steps = run.Steps,
    };
}

/// One entry of §7.1's `ThreadEntry` union.
///
/// `Seq` is settable and every other member is init-only, on purpose: the sequence number is not a
/// property of the entry, it is its POSITION, and position is only known once both halves have been
/// merged and ordered. Stamping it afterwards is the one mutation.
public abstract record ThreadEntry
{
    /// Assigned by <see cref="ThreadEndpoints.BuildThreadAsync"/>, dense from 1. See the note there on
    /// why it is not derived from the timestamp.
    public int Seq { get; set; }
    /// When this entry HAPPENED — strictly truthful, and not necessarily the key it was sorted on
    /// (see BuildThreadAsync's anchor handling for a reply).
    public required string At { get; init; }
    public abstract string Kind { get; }
}

public sealed record ThreadRunEntry : ThreadEntry
{
    public override string Kind => "run";
    public required RunSummary Run { get; init; }
}

public sealed record ThreadMessageEntry : ThreadEntry
{
    public override string Kind => "message";
    /// `operator` | `agent` (<see cref="ChatRoles"/>).
    public required string Role { get; init; }
    public required string Text { get; init; }
    /// `queued` | `answered` | `failed`.
    public required string Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Error { get; init; }
}

/// The unified per-stage thread — 2026-07-27-execution-core-design.md §7.
///
/// A1 serves this contract over the EXISTING chat storage: runs come from IRunStore and messages from the
/// ChatMessageDoc thread, merged here by timestamp. A2 replaces the storage with one genuine thread and
/// deletes the merge; the SHAPE does not change, which is precisely what lets the web track integrate now
/// rather than after A2.
///
/// §7.3's control half lives here too: the message door, cancel, and rerun. All three take the
/// PipelineSupervisor as a REQUIRED service — none of them has a meaningful answer without it, and a host
/// that forgot the registration should say so loudly rather than return 202 over a pipeline nobody started.
/// (POST /projects/{id}/start takes it optionally, for the opposite reason — see the note there.)
public static class ThreadEndpoints
{
    public static void MapThreadEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the top
        // of ProjectEndpoints. Without it, minimal APIs infer service-vs-body via IServiceProviderIsService
        // at endpoint-build time across the WHOLE app's composite endpoint data source, mis-infer these as
        // body params (illegal on GET), and break routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/stages/{stage}/thread", async (
            string projectId, string stage,
            [FromServices] IRunStore runs, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                });
            // No project existence check, deliberately: an unknown project reads as an EMPTY thread, not a
            // 404 — the same call the chat read already makes (ChatEndpoints), and the client's seed path
            // treats 404 as an error state rather than "nothing has happened yet".
            return Results.Json(await BuildThreadAsync(projectId, stage, runs, store, ct), Json.Options);
        });

        app.MapGet("/projects/{projectId}/stages/{stage}/thread/stream", async (
            string projectId, string stage, string? since, HttpContext http,
            [FromServices] ThreadEventHub hub, [FromServices] IRunStore runs, CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
            {
                http.Response.StatusCode = 422;
                await http.Response.WriteAsJsonAsync(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                }, ct);
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            // SUBSCRIBE FIRST, then replay. The other order has a hole exactly the width of the replay
            // query: a step published while the catch-up was in flight would reach no subscriber and never
            // be replayed either, and the operator would be missing a step with no way to know it. The cost
            // of this order is a duplicate — a frame both replayed and delivered live — and the client
            // reconciles on the frame id, so a duplicate is idempotent. A gap is not.
            using var subscription = hub.Subscribe(projectId, stage);
            foreach (var frame in await ReplayAsync(projectId, stage, since, runs, ct))
                await WriteAsync(http, frame, ct);

            // ONE writer, always. The obvious shape — a background Task.Run pumping heartbeats beside the
            // foreground loop pumping frames — has two tasks writing the same HttpResponse concurrently,
            // and Response.WriteAsync is not thread-safe: interleaved writes produce a torn SSE frame,
            // which the client's parser either drops or mis-attributes. So the two sources are multiplexed
            // into this single loop instead.
            var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                var read = subscription.Reader.WaitToReadAsync(ct).AsTask();
                var tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                while (true)
                {
                    if (await Task.WhenAny(read, tick) == read)
                    {
                        if (!await read) break;   // the hub closed the channel
                        while (subscription.Reader.TryRead(out var frame)) await WriteAsync(http, frame, ct);
                        read = subscription.Reader.WaitToReadAsync(ct).AsTask();
                    }
                    else
                    {
                        if (!await tick) break;
                        // App Gateway reaps an idle connection, and a long tool call is a long idle. A ':'
                        // comment is a no-op frame the client's parser already skips.
                        await http.Response.WriteAsync(": keep-alive\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                    }
                }
            }
            catch (OperationCanceledException) { /* the client navigated away */ }
            finally { heartbeat.Dispose(); }
        });

        // §7.3's replay/audit read: every run for the project, or for one stage, oldest first. Distinct from
        // the thread above, which is per-stage and merges the conversation in — this one crosses the whole
        // project and carries runs only.
        //
        // Through RunSummary, like every other run on the wire: the doc calls the identifier `id` and carries
        // the partition key, and a client coding against §7 would read `runId === undefined` on every row.
        app.MapGet("/projects/{projectId}/runs", async (
            string projectId, string? stage, [FromServices] IRunStore runs, CancellationToken ct) =>
        {
            if (stage is { Length: > 0 } && !Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                });
            var docs = await runs.ListAsync(projectId, string.IsNullOrEmpty(stage) ? null : stage, ct);
            return Results.Json(docs.Select(RunSummary.From).ToList(), Json.Options);
        });

        // §7.3 — the operator's message. A1 has no mailbox (design §5; that is A2), so this RECORDS the turn
        // and does not run it: `queued` is the truthful status, and the endpoint says so in the response.
        // What it must never do is answer inline — a POST that spends minutes in Foundry on the caller's
        // thread is the shape this design exists to remove.
        app.MapPost("/projects/{projectId}/stages/{stage}/messages", async (
            string projectId, string stage, ChatRequest req,
            [FromServices] IRecordStore store, [FromServices] IRunStore runs,
            [FromServices] PipelineSupervisor supervisor, CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                });
            // Blank text before any store lookup, as the chat door does: an empty turn is always a 422, never
            // a 404 — and it must not reach the record, because nothing downstream re-checks it.
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.UnprocessableEntity(new { error = "a message cannot be blank" });
            if (await store.GetProjectAsync(projectId, ct) is null) return Results.NotFound();

            // The suffix must be an ID-SAFE token — a cross-service contract, not a style choice. See the
            // long note in ChatEndpoints: the key is concatenated into further Cosmos ids downstream, and
            // Cosmos rejects '/', '\', '?' and '#' with a 400 no in-memory test store can produce.
            var messageId = RecordIds.ChatMessage(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            var createdAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertChatMessageAsync(new ChatMessageDoc
            {
                Id = messageId, ProjectId = projectId, Stage = stage, Text = req.Text,
                Status = ChatStatus.Pending,
                // ALWAYS "O". This is the thread's SORT KEY, compared lexicographically, and it is only
                // chronological while every writer uses the same fixed-width format.
                CreatedAt = createdAt,
            }, ct);

            // The seq is the message's POSITION IN THE MERGED THREAD, read back after the write — not a count
            // of messages. The client dedupes the thread it already holds against this number, so a seq that
            // ignored the runs would file the new message above entries that came before it.
            var thread = await BuildThreadAsync(projectId, stage, runs, store, ct);
            var seq = thread.OfType<ThreadMessageEntry>()
                .LastOrDefault(m => m.At == createdAt && m.Text == req.Text)?.Seq ?? thread.Count;

            // NOT PUBLISHED TO THE HUB, deliberately. Every frame id in the stream lives in the {runId} cursor
            // space that ReplayAsync rebuilds; a message has no run and no replayable id, so a client that
            // echoed one back as `?since=` would fall through to "unrecognised cursor" and replay the whole
            // trail. The message reaches the client on this response and on the next §7.1 read. A2's unified
            // thread gives message entries a real id, and this becomes a live frame then.
            return Results.Accepted($"/projects/{projectId}/stages/{stage}/thread", new
            {
                messageId,
                seq,
                // true ⇒ a run is in flight, so the UI can say the agent will get to this rather than
                // implying an answer is imminent.
                queued = supervisor.IsRunning(projectId),
            });
        });

        app.MapPost("/projects/{projectId}/runs/{runId}/cancel", async (
            string projectId, string runId,
            [FromServices] IRunStore runs, [FromServices] PipelineSupervisor supervisor,
            CancellationToken ct) =>
        {
            if (await runs.GetAsync(projectId, runId, ct) is not { } run) return Results.NotFound();
            if (run.Outcome != RunOutcome.Running)
                return Results.Conflict(new { error = $"that run is already {run.Outcome}" });
            // Cancelling one substance of fourteen leaves a candidate set that LOOKS screened and is not. The
            // parent is the only granularity at which this is honest.
            if (run.ParentRunId is not null)
                return Results.UnprocessableEntity(new
                {
                    error = "cancel the regulatory stage's parent run — cancelling one substance would " +
                            "leave a partially screened candidate set that reads as complete",
                });
            // `running` in the store but held by no process: the trail of a dead process. There is nothing to
            // cancel, and a 202 that did nothing would be worse than saying so.
            return supervisor.CancelRun(runId)
                ? Results.Accepted()
                : Results.Conflict(new { error = "that run is not live — no process is holding it" });
        });

        app.MapPost("/projects/{projectId}/stages/{stage}/rerun", async (
            string projectId, string stage,
            [FromServices] IRecordStore store, [FromServices] PipelineSupervisor supervisor,
            CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                });
            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();
            if (!project.Stages.TryGetValue(stage, out var state))
                return Results.UnprocessableEntity(new { error = $"this project has no '{stage}' stage" });

            // `done` is refused deliberately, and so is every park. Re-running a landed stage replaces
            // analysis a gate may have been signed over, and revise-with-reason is the path that does that
            // WITH the operator's reason recorded. Rerun must not become the backdoor around it.
            if (state.Status is not ("failed" or "needs-review" or "cancelled"))
                return Results.UnprocessableEntity(new
                {
                    error = $"the {stage} stage is '{state.Status}' — only a failed, needs-review or " +
                            "cancelled stage can be re-run. To change a landed result, tell the agent why, " +
                            "so the reason is recorded.",
                });

            // Checked BEFORE the reset: writing `pending` under a live pipeline is a write racing the runner
            // over the same stage state, and the answer is the same 409 a second start gets anyway.
            if (supervisor.IsRunning(projectId))
                return Results.Conflict(new { error = "a pipeline is already running for this project" });

            // The reset is what makes rerun mean anything. The runner re-enters a stage only from `pending`
            // or `running` — `failed` and `needs-review` are states it deliberately never picks up on its own
            // pass, so without this the pipeline would walk straight past the stage the operator just asked
            // for and land back where it started, having reported 202.
            state.Status = StageStatus.Pending;
            state.Error = null;
            await store.UpsertProjectAsync(project, ct);

            return supervisor.TryStart(projectId)
                ? Results.Accepted()
                : Results.Conflict(new { error = "a pipeline is already running for this project" });
        });
    }

    /// The merged transcript, §7.1. In A1 the two halves live in two stores — runs in IRunStore, messages
    /// in the ChatMessageDoc thread — so they are merged HERE.
    ///
    /// `seq` is assigned here, dense from 1, and is the client's dedupe key. It is deliberately NOT derived
    /// from a timestamp: two records written in the same millisecond would collide, and a collided seq makes
    /// the client drop an entry as "already held".
    ///
    /// THE CHAT HALF IS NOT RE-SORTED BY CreatedAt, and that is the whole subtlety of this function.
    /// GetChatThreadAsync already returns the thread in ChatTurns.InOrder, where a REPLY is anchored to the
    /// message it answers rather than to its own clock — because a reply is stamped when the turn ENDS,
    /// tens of seconds later, by which time the operator may have posted again. Re-sorting on CreatedAt
    /// here would put the answer to the first question under the second one, which is the exact bug
    /// ChatTurns.InOrder's long comment exists to describe. So each turn's merge key is the running MAXIMUM
    /// of CreatedAt in InOrder order: non-decreasing by construction, hence a stable sort preserves
    /// InOrder exactly, while still placing the conversation sensibly against the runs. `at` on the wire
    /// stays the turn's own CreatedAt — truthful about when it was written, as ChatTurns insists.
    internal static async Task<List<object>> BuildThreadAsync(
        string projectId, string stage, IRunStore runs, IRecordStore store, CancellationToken ct)
    {
        var runDocs = await runs.ListAsync(projectId, stage, ct);
        var turns = await store.GetChatThreadAsync(projectId, stage, ct);

        // Runs go in first so that on an exact timestamp tie the stable sort puts the run entry above the
        // message — arbitrary, but deterministic, and determinism is the property that matters when the
        // same thread is re-read on the next poll.
        var merged = new List<(string Key, ThreadEntry Entry)>(runDocs.Count + turns.Count);
        foreach (var run in runDocs)
            merged.Add((run.StartedAt, new ThreadRunEntry { At = run.StartedAt, Run = RunSummary.From(run) }));

        var anchor = "";
        foreach (var turn in turns)
        {
            // Ordinal, not culture-aware: these are fixed-width round-trip "O" timestamps compared as text,
            // and a culture-sensitive comparison can reorder them (the ChatTurns.InOrder lesson).
            if (string.CompareOrdinal(turn.CreatedAt, anchor) > 0) anchor = turn.CreatedAt;
            merged.Add((anchor, new ThreadMessageEntry
            {
                At = turn.CreatedAt,
                Role = turn.Role,
                Text = turn.Text,
                // A1 has no `queued` concept yet — the mailbox is A2. `pending` maps to `queued` so the
                // client's three-state union holds from day one and does not change under it later.
                Status = turn.Status == ChatStatus.Pending ? "queued" : turn.Status,
                Error = turn.Error,
            }));
        }

        // OrderBy is a STABLE sort, which is load-bearing twice over: it is what preserves ChatTurns.InOrder
        // within the chat half, and what makes the run-before-message tie-break above hold.
        var ordered = merged.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Entry).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Seq = i + 1;

        // `List<object>`, NOT `List<ThreadEntry>`. With a declared element type of ThreadEntry,
        // System.Text.Json serializes the BASE type's properties only and every entry would go out as
        // `{ seq, at, kind }` with the payload silently missing. `object` makes it use the runtime type.
        // Do not "tidy" this into the typed list.
        return [.. ordered.Cast<object>()];
    }

    /// The catch-up a reconnecting client needs: every frame the live stream would have sent, for the runs
    /// already on file, with everything at or before `since` dropped.
    ///
    /// Ids match RunTrail's live path EXACTLY ("{runId}" for the entry, "{runId}.s{seq}", "{runId}.r") —
    /// verified against RunTrail.cs, not assumed. They are one cursor space: a replayed id the client cannot
    /// match against what it already holds would duplicate rather than dedupe.
    internal static async Task<List<ThreadFrame>> ReplayAsync(
        string projectId, string stage, string? since, IRunStore runs, CancellationToken ct)
    {
        var frames = new List<ThreadFrame>();
        foreach (var run in await runs.ListAsync(projectId, stage, ct))
        {
            // seq 0, matching RunTrail.OpenAsync: at the moment a run opens, its position in the merged
            // thread is not yet known. The client reconciles a run entry on its runId.
            frames.Add(new ThreadFrame("entry", run.Id,
                new ThreadRunEntry { Seq = 0, At = run.StartedAt, Run = RunSummary.From(run) }));
            foreach (var step in run.Steps)
                frames.Add(new ThreadFrame("step", $"{run.Id}.s{step.Seq}", new { runId = run.Id, step }));
            if (run.EndedAt is not null)
                frames.Add(new ThreadFrame("run", $"{run.Id}.r",
                    new { runId = run.Id, endedAt = run.EndedAt, outcome = run.Outcome, error = run.Error }));
        }

        if (string.IsNullOrEmpty(since)) return frames;
        var cursor = frames.FindIndex(f => f.Id == since);
        // An unrecognised cursor replays EVERYTHING rather than nothing. A client resuming with an id from a
        // run that has since been superseded would otherwise silently receive an empty stream and sit there
        // looking connected and blank; a duplicate is idempotent on the client, a gap is not.
        return cursor < 0 ? frames : [.. frames.Skip(cursor + 1)];
    }

    private static async Task WriteAsync(HttpContext http, ThreadFrame frame, CancellationToken ct)
    {
        await http.Response.WriteAsync(
            $"event: {frame.Event}\nid: {frame.Id}\ndata: {JsonSerializer.Serialize(frame.Data, Json.Options)}\n\n",
            ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
