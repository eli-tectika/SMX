using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateIntakeSessionRequest(string? Client, string? Product);

/// The pre-project interview's session CRUD. The streaming turn itself lives in InterviewEndpoints, in
/// this same app: it used to be a relay to a separate orchestrator process, and that relay existed only
/// because this app could not run an agent. It can now.
public static class IntakeSessionEndpoints
{
    public static void MapIntakeSessionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at
        // the top of ProjectEndpoints. Without it, minimal APIs mis-infer these as body params and
        // break routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/intake-sessions", async (
            CreateIntakeSessionRequest req, [FromServices] IIntakeSessionStore sessions, CancellationToken ct) =>
        {
            var id = RecordIds.NewIntakeSessionId();
            await sessions.UpsertAsync(new IntakeSessionDoc
            {
                Id = id, SessionId = id,
                Client = req.Client?.Trim() ?? "", Product = req.Product?.Trim() ?? "",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Created($"/intake-sessions/{id}", new { sessionId = id });
        });

        app.MapGet("/intake-sessions/{sessionId}", async (
            string sessionId, [FromServices] IIntakeSessionStore sessions, CancellationToken ct) =>
            await sessions.GetAsync(sessionId, ct) is { } s
                ? Results.Json(s, Json.Options)
                : Results.NotFound());
    }
}
