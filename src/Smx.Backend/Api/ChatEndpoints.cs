using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record ChatRequest(string Text);

/// The per-stage conversation (design §5). The operator talks to the agent that produced the stage they are
/// looking at — one thread per (project, stage), because the stage agents do not share a conversation (Law 9)
/// and neither do their threads.
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every IRecordStore param below is required, not decorative — see the long comment
        // at the top of ProjectEndpoints. Minimal APIs infer service-vs-body via IServiceProviderIsService at
        // endpoint-build time, across the WHOLE app's composite endpoint data source; without it a test host
        // that registers only IKnowledgeStore mis-infers these as body params (illegal on GET) and breaks
        // routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/projects/{projectId}/stages/{stage}/chat",
            async (string projectId, string stage, ChatRequest req, [FromServices] IRecordStore store,
                   [FromServices] PipelineSupervisor? supervisor, CancellationToken ct) =>
        {
            // A chat-message on an unknown stage is a doc the dispatcher would run ANYWAY: it would find no
            // stage inputs (StageInputsJsonAsync falls through to "{}") and no stage read tools, and the agent
            // would hold a confident conversation about nothing. The door is the only place to stop it.
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"unknown stage '{stage}' — one of: {string.Join(", ", Stages.All)}",
                });
            // Blank text checked before any store lookup, as in RevisionEndpoints: an empty turn is always a
            // 422, never a 404. And it must not reach the bus — the change feed does not care that the message
            // is empty, so the agent would be handed nothing to answer and would answer it regardless.
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.UnprocessableEntity(new { error = "a chat message cannot be blank" });

            if (await store.GetProjectAsync(projectId, ct) is null) return Results.NotFound();

            // The suffix must be an ID-SAFE token ([A-Za-z0-9_-]+), and that is a cross-service contract, not a
            // local style choice: PipelineRunner derives the chat key from this id's last '|'-segment and hands
            // it to ChatTools, which concatenates it into further Cosmos item ids. Cosmos rejects an id
            // containing '/', '\', '?' or '#' — a 400 that no in-memory test store can produce. A "friendlier"
            // scheme (a slug of the text, a timestamp with ':') would pass every backend test and break every
            // chat turn in Azure. ChatEndpointsTests pins it.
            var messageId = RecordIds.ChatMessage(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            var doc = new ChatMessageDoc
            {
                Id = messageId,
                ProjectId = projectId,
                Stage = stage,
                Text = req.Text,
                // `pending` is the dispatcher's ONLY idempotency guard on an at-least-once feed
                // (OnChatMessageAsync returns early on any other status). A message written in any other
                // status is a message that is never answered.
                Status = ChatStatus.Pending,
                // ALWAYS "O". This is the thread's SORT KEY — for this message and for the reply anchored to it
                // (ChatTurns.InOrder) — and it is compared LEXICOGRAPHICALLY, which is only chronological while
                // every writer uses the same fixed-width format (see ChatMessageDoc.CreatedAt). This endpoint
                // and the orchestrator's reply writer are the two writers of that thread; they must agree, or
                // the transcript lies about who said what first.
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            await store.UpsertChatMessageAsync(doc, ct);

            // AND NOW THE TURN ACTUALLY RUNS. Writing the doc used to BE the dispatch: the change feed
            // picked the message up, ran the turn and wrote the reply. Nothing watches the record any more,
            // so between the feed's removal and this line every question the operator asked was recorded
            // into the transcript and never answered.
            //
            // DETACHED, not in the project's pipeline slot, and that is the whole point of the distinction:
            // the operator asks "why did you drop the Zr neodecanoate?" WHILE Regulatory is screening. A
            // turn that queued behind the pipeline would answer minutes later, or — if the run parks — not
            // at all. It is also not inline: RunChatAsync is a model call with a tool loop behind it.
            //
            // The turn is handed the doc we just wrote, but OnChatMessageAsync point-reads the message by
            // id and acts only on a `pending` one, so this is a trigger, not a payload: a second POST cannot
            // produce a second answer to the same message, and neither can a retry.
            if (supervisor is { } sup)
                sup.RunDetached($"chat turn {messageId}", async (runner, token) =>
                {
                    await runner.OnChatMessageAsync(doc, token);
                    // A turn can leave WORK behind, not just words: `apply_revision` writes a durable pending
                    // RevisionDoc and tells the operator the stage will re-run. Nothing in the turn applies
                    // it — the turn deliberately holds no project slot — so this claims the slot and drains.
                    // On a busy project it steps aside and the slot-holder's own drain does it instead.
                    await sup.DrainWhenFreeAsync(projectId, token);
                });

            // 202, not 200: the turn is running, not finished. Nothing has been answered yet; the UI polls
            // the GET below for the reply.
            return Results.Accepted($"/projects/{projectId}/stages/{stage}/chat",
                new { messageId, status = ChatStatus.Pending });
        });

        // The thread, oldest-first — the operator's messages and the agent's replies merged into one
        // transcript (IRecordStore.GetChatThreadAsync, ordered by ChatTurns.InOrder).
        //
        // Scoped to ONE stage, and an unknown project yields an empty thread rather than a 404 — like the
        // trail at GET /projects/{id}/revisions. A thread is a collection: "nothing has been said here" is the
        // honest answer, and the typo'd project id is caught on the POST above, which is where it matters.
        app.MapGet("/projects/{projectId}/stages/{stage}/chat",
            async (string projectId, string stage, [FromServices] IRecordStore store, CancellationToken ct) =>
                Results.Json(await store.GetChatThreadAsync(projectId, stage, ct), Json.Options));
    }
}
