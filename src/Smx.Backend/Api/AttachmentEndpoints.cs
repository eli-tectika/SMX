using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Extraction;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// Upload a file into a pre-project interview. Extraction runs HERE, synchronously, inside the request:
/// no agent, no dispatch, no queue (design §5.1). A 25 MB ceiling is what keeps that honest.
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the
        // top of ProjectEndpoints. Without it, minimal APIs mis-infer these as body params and break
        // routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/intake-sessions/{sessionId}/attachments", async (
            string sessionId, IFormFile file, HttpContext http,
            [FromServices] IIntakeSessionStore sessions,
            [FromServices] IAttachmentBlobStore blobs,
            [FromServices] TextExtraction extraction,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.UnprocessableEntity(new { error = "no file was uploaded" });

            if (file.Length > AttachmentLimits.MaxFileBytes)
                return Results.UnprocessableEntity(new
                {
                    error = $"'{file.FileName}' is larger than the {AttachmentLimits.MaxFileBytes / (1024 * 1024)} MB limit.",
                });

            if (await sessions.GetAsync(sessionId, ct) is not { } session) return Results.NotFound();

            if (session.Attachments.Count >= AttachmentLimits.MaxFilesPerSession)
                return Results.UnprocessableEntity(new
                {
                    error = $"this interview already has {AttachmentLimits.MaxFilesPerSession} attachments, " +
                            "which is the limit.",
                });

            // The fileId, not the filename, is what makes the path unique — the operator re-uploads a
            // corrected "questionnaire.pdf" and the first one must stay exactly where it was, or an
            // earlier citation silently starts pointing at different content.
            var fileId = RecordIds.NewAttachmentId();
            var blobPath = AttachmentPaths.Blob(sessionId, fileId, file.FileName);

            // Bytes FIRST, then extraction. If extraction fails, the file is still stored and still
            // visible to the agent as a `failed` attachment it can ask about — which is the whole
            // discipline of §5.2. The other order loses the file whenever the parser dislikes it.
            await using (var upload = file.OpenReadStream())
                await blobs.PutAsync(blobPath, upload, file.ContentType ?? "", ct);

            ExtractionResult result;
            await using (var forExtraction = file.OpenReadStream())
                result = await extraction.ExtractAsync(
                    file.FileName, file.ContentType ?? "", forExtraction, ct);

            string? textPath = null;
            if (result.Status == AttachmentStatus.Extracted)
            {
                textPath = AttachmentPaths.Text(sessionId, fileId);
                await blobs.PutTextAsync(textPath, result.Text, ct);
            }

            var attachment = new SessionAttachment
            {
                FileId = fileId,
                Filename = AttachmentPaths.SafeFilename(file.FileName),
                ContentType = file.ContentType ?? "",
                SizeBytes = file.Length,
                BlobPath = blobPath,
                TextBlobPath = textPath,
                Status = result.Status,
                Error = result.Error,
            };

            // Re-read before appending: an upload can land while an interview turn is in flight, and
            // writing back the copy fetched above would discard whatever the agent recorded meanwhile.
            var latest = await sessions.GetAsync(sessionId, ct) ?? session;
            latest.Attachments.Add(attachment);
            latest.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await sessions.UpsertAsync(latest, ct);

            return Results.Created($"/intake-sessions/{sessionId}/attachments/{fileId}", attachment);
        })
        // REQUIRED. .NET 8 minimal APIs reject a multipart form post with a 400 unless antiforgery is
        // disabled on the endpoint, and the failure reads as a malformed request rather than a missing
        // configuration. This surface is same-origin behind App Gateway and JWT-authenticated; there is
        // no cookie auth for a forged cross-site post to ride on.
        .DisableAntiforgery();
    }
}
