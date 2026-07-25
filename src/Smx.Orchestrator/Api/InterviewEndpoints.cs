using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Api;

public sealed record InterviewMessageRequest(string Text);

/// The orchestrator's ONLY HTTP surface, and it exists for one reason: an interview turn must stream.
/// Everything else in this host is still change-feed driven.
///
/// Reached only from the backend, over internal Container Apps networking. Auth is NOT re-checked here
/// — the backend validates the JWT and this surface is not routable from outside the environment.
/// Duplicating token validation in a second host would mean two places to get an audience wrong.
public static class InterviewEndpoints
{
    public static void MapInterviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/intake-sessions/{sessionId}/messages", async (
            string sessionId, InterviewMessageRequest req, HttpContext http,
            [FromServices] IIntakeSessionStore sessions, [FromServices] IRecordStore records,
            [FromServices] IAttachmentBlobStore blobs, [FromServices] IAgentRuns runs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                http.Response.StatusCode = 422;
                await http.Response.WriteAsJsonAsync(new { error = "a message cannot be blank" }, ct);
                return;
            }
            if (await sessions.GetAsync(sessionId, ct) is not { } session)
            {
                http.Response.StatusCode = 404;
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            // The operator's turn is persisted BEFORE the agent runs. If the model call fails, what
            // they said is still in the record — losing the operator's own words to an upstream 429
            // would be the worst possible failure of Law 6.
            session.Turns.Add(new InterviewTurn
            {
                Role = "operator", Text = req.Text, CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            await sessions.UpsertAsync(session, ct);

            var tools = new InterviewTools(sessions, records, blobs, sessionId);
            var reply = new System.Text.StringBuilder();
            await foreach (var chunk in runs.RunInterviewAsync(tools, session, req.Text, ct)
                               .WithCancellation(ct))
            {
                reply.Append(chunk);
                await WriteEventAsync(http, "chunk", new { text = chunk }, ct);
            }

            // Re-read: the tools mutated the session document while the turn ran, and `session` is a
            // stale copy from before. Writing that copy back would silently discard every finding the
            // agent just recorded.
            var latest = await sessions.GetAsync(sessionId, ct) ?? session;
            latest.Turns.Add(new InterviewTurn
            {
                Role = "agent", Text = reply.ToString(), ToolCalls = tools.Trail,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            latest.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await sessions.UpsertAsync(latest, ct);

            await WriteEventAsync(http, "done",
                new { createdProjectId = latest.CreatedProjectId, toolCalls = tools.Trail }, ct);
        });
    }

    private static async Task WriteEventAsync(HttpContext http, string name, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json.Options)}\n\n", ct);
        // Flush per event or the whole point is lost: a buffered response arrives in one lump and the
        // operator watches a spinner, which is the outcome this endpoint exists to avoid.
        await http.Response.Body.FlushAsync(ct);
    }
}
