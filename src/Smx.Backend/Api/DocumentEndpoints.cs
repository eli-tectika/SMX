using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Smx.Domain;
using Smx.Domain.Documents;

namespace Smx.Backend.Api;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative — see the comment in KnowledgeEndpoints: without
        // it, minimal APIs may infer the service as a request body, which is illegal on GET and
        // breaks routing for the ENTIRE app while building the composite endpoint data source.
        app.MapGet("/documents", async (string? kind, string? q, string? state,
            [FromServices] IDocumentCatalog catalog, CancellationToken ct) =>
        {
            var filter = new DocumentFilter(
                Kind: kind is { Length: > 0 } ? kind : DocumentKinds.All,
                Q: q,
                State: state is { Length: > 0 } ? state : DocumentStates.All);
            return Results.Json(await catalog.ListAsync(filter, ct), Json.Options);
        });

        app.MapGet("/documents/{id}", async (string id, [FromServices] IDocumentCatalog catalog,
            [FromServices] IDocumentContentStore store, CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();

            // Drift check: the registry says this document exists. If storage disagrees, say so
            // here rather than letting the viewer discover it as a blank pane.
            if (detail.BlobPath is not null && await store.ReadAsync(detail.BlobPath, ct) is null)
                detail = detail with
                {
                    Summary = detail.Summary with { Available = false, State = DocumentStates.Missing },
                    UnavailableReason = UnavailableReasons.BlobMissing,
                    UnavailableDetail = "The registry lists this document but it is not in storage.",
                };

            return Results.Json(ToWire(detail), Json.Options);
        });

        // `download` is bound as a string, not a bool?: minimal APIs parse a bool? with bool.TryParse,
        // which rejects the "1" a download link naturally carries and answers 400 for it — a flag that
        // refuses the form everyone writes is a trap, so accept the flag spellings explicitly.
        app.MapGet("/documents/{id}/content", async (string id, string? download, HttpContext http,
            [FromServices] IDocumentCatalog catalog, [FromServices] IDocumentContentStore store,
            CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            // Valid id, knowably absent document. Not 404 (the record exists) and not 500 (nothing
            // failed) — 409 is the state itself.
            if (detail.BlobPath is null) return Results.Conflict(new { reason = detail.UnavailableReason });

            var opened = await store.OpenAsync(detail.BlobPath, ct);
            if (opened is null) return Results.NotFound();

            var contentType = detail.Summary.ContentType ?? "application/octet-stream";
            var wantsDownload = IsFlagSet(download);
            var fileName = FileNameFor(detail);

            // Results.Stream writes a Content-Disposition only when handed a download name, and only
            // as `attachment`. The viewer's whole job is to show the document in place, so state
            // `inline` rather than leaving the browser to infer it from the content type.
            if (!wantsDownload)
            {
                var inline = new ContentDispositionHeaderValue("inline");
                inline.SetHttpFileName(fileName);
                http.Response.Headers.ContentDisposition = inline.ToString();
            }

            var response = Results.Stream(opened.Stream, contentType,
                fileDownloadName: wantsDownload ? fileName : null,
                enableRangeProcessing: true);
            return response;
        }).AddEndpointFilter(async (ctx, next) =>
        {
            // Applied as a filter so the headers are set even on the streaming path, where the result
            // writes the body itself.
            ctx.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.HttpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return await next(ctx);
        });

        app.MapGet("/documents/{id}/text", async (string id, [FromServices] IDocumentCatalog catalog,
            [FromServices] IDocumentTextReader reader, CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            if (detail.BlobPath is null) return Results.Conflict(new { reason = detail.UnavailableReason });

            // An empty list is the honest answer for a document that reached bronze but never the
            // index — it means no agent has ever read it, and the viewer says so.
            return Results.Json(await reader.ReadChunksAsync(detail, ct), Json.Options);
        });
    }

    /// `?download`, `?download=1`, `?download=true` all mean the same thing; anything else — including
    /// `0` and `false` — does not. An allow-list, so an unrecognised value never reads as "yes".
    private static bool IsFlagSet(string? value) =>
        value is "" or "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Windows opens CON/PRN/AUX/NUL/COM1-9/LPT1-9 as devices even with an extension appended, so a
    // document titled "CON" would suggest a save name the operator's own OS refuses. The title comes
    // from Cosmos rather than from the URL, so this is data hygiene, not an injection defence.
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// A download filename derived from the document, never from client input.
    private static string FileNameFor(DocumentDetail d)
    {
        var ext = (d.Summary.ContentType ?? "") switch
        {
            var c when c.Contains("pdf", StringComparison.OrdinalIgnoreCase) => "pdf",
            var c when c.Contains("html", StringComparison.OrdinalIgnoreCase) => "html",
            var c when c.Contains("csv", StringComparison.OrdinalIgnoreCase) => "csv",
            var c when c.Contains("json", StringComparison.OrdinalIgnoreCase) => "json",
            var c when c.Contains("xml", StringComparison.OrdinalIgnoreCase) => "xml",
            _ => "txt",
        };
        var stem = new string(d.Summary.Title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        // Every non-alphanumeric becomes '-', so a path separator, a dot run, or a quote cannot reach
        // the header; what survives the trim can still be empty, or a device name.
        if (stem.Length == 0) stem = "document";
        if (stem.Length > 80) stem = stem[..80];
        if (ReservedFileNames.Contains(stem)) stem = $"document-{stem}";
        return $"{stem}.{ext}";
    }

    /// The wire shape drops BlobPath. Returning it would hand the client the exact string the id
    /// scheme exists to keep out of the API surface.
    internal static object ToWire(DocumentDetail d) => new
    {
        summary = d.Summary,
        provenance = d.Provenance,
        unavailableReason = d.UnavailableReason,
        unavailableDetail = d.UnavailableDetail,
        supersededById = d.SupersededById,
    };
}
