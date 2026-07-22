using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateIntakeSessionRequest(string? Client, string? Product);

public sealed record InterviewMessageBody(string Text);

/// The pre-project interview's front door. The backend owns session CRUD and JWT validation; the
/// orchestrator owns the agent. The message route is a PROXY — the backend cannot run an agent, and
/// the orchestrator is not publicly routable, so the stream passes through here.
public static class IntakeSessionEndpoints
{
    /// The named HttpClient the proxy is built over, pointed at the orchestrator's internal FQDN.
    public const string OrchestratorClient = "orchestrator";

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

        // The SSE proxy. ResponseHeadersRead + a copied body, NOT ReadAsStringAsync: buffering the
        // orchestrator's stream here would collapse it into one lump and defeat the entire feature.
        app.MapPost("/intake-sessions/{sessionId}/messages", async (
            string sessionId, InterviewMessageBody body, HttpContext http,
            [FromServices] IHttpClientFactory factory, CancellationToken ct) =>
        {
            var upstream = factory.CreateClient(OrchestratorClient);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/internal/intake-sessions/{sessionId}/messages")
            {
                Content = JsonContent.Create(body),
            };
            using var response = await upstream.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(http.Response.Body, ct);
        });
    }
}
