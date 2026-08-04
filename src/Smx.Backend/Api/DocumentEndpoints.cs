using Microsoft.Net.Http.Headers;
using Smx.Domain;
using Smx.Domain.Documents;

namespace Smx.Backend.Api;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // The document services are resolved PER REQUEST from HttpContext.RequestServices rather than
        // through [FromServices]. Two configuration faults otherwise reach the operator as a bare 500
        // that names nothing they can act on: bronze unconfigured (the store's factory throws by
        // design, with a message naming the variable) and a deployment with no COSMOS_ACCOUNT_ENDPOINT
        // at all, where the whole block is skipped and DI answers "No service for type
        // IDocumentCatalog". Spec §8 asks for a 503 with a plain message in the first case; the second
        // is the same class of fault and gets the same answer.
        app.MapGet("/documents", async (string? kind, string? q, string? state, HttpContext http,
            CancellationToken ct) =>
        {
            // An unrecognised filter value must never read as "no documents". This feature exists so
            // absence cannot pass for coverage, and a typo'd ?kind= answering 200 [] is that same
            // failure in miniature.
            if (Unknown(kind, KnownKinds) is { } badKind)
                return Results.BadRequest(new { error = $"unknown kind '{badKind}'", allowed = KnownKinds });
            if (Unknown(state, KnownStates) is { } badState)
                return Results.BadRequest(new { error = $"unknown state '{badState}'", allowed = KnownStates });

            if (!TryResolve<IDocumentCatalog>(http, out var catalog, out var unavailable)) return unavailable!;

            var filter = new DocumentFilter(
                Kind: kind is { Length: > 0 } ? kind : DocumentKinds.All,
                Q: q,
                State: state is { Length: > 0 } ? state : DocumentStates.All);
            return Results.Json(await catalog.ListAsync(filter, ct), Json.Options);
        });

        app.MapGet("/documents/{id}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            if (!TryResolve<IDocumentCatalog>(http, out var catalog, out var unavailable)) return unavailable!;
            if (!TryResolve<IDocumentContentStore>(http, out var store, out unavailable)) return unavailable!;

            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();

            // Drift check: the registry says this document exists. If storage disagrees, say so
            // here rather than letting the viewer discover it as a blank pane. ExistsAsync, not
            // ReadAsync — the question is a boolean and downloading the document to answer it would
            // cost the document's full size on every detail view.
            if (detail.BlobPath is not null && !await store.ExistsAsync(detail.BlobPath, ct))
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
            CancellationToken ct) =>
        {
            if (!TryResolve<IDocumentCatalog>(http, out var catalog, out var unavailable)) return unavailable!;
            if (!TryResolve<IDocumentContentStore>(http, out var store, out unavailable)) return unavailable!;

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

            // No enableRangeProcessing. It would be a no-op on the only stream production ever hands
            // this: FileStreamHttpResult serves ranges only when stream.CanSeek, and the ADLS read
            // stream cannot seek — so the flag bought a 206 in tests (whose fake yields a MemoryStream)
            // and a plain 200 in Azure. A browser that asks for a range and gets the whole document
            // renders it; a flag that claims a capability the deployment does not have is the hazard.
            // Real range support means an OpenRangeAsync on IDocumentContentStore over
            // DataLakeFileClient.ReadAsync(HttpRange) — worth doing if the viewer ever streams video
            // or pages a very large PDF, and not before (spec §7 fetches whole documents).
            return Results.Stream(opened.Stream, contentType,
                fileDownloadName: wantsDownload ? fileName : null);
        }).AddEndpointFilter(async (ctx, next) =>
        {
            // Applied as a filter so the headers are set even on the streaming path, where the result
            // writes the body itself.
            ctx.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.HttpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return await next(ctx);
        });

        app.MapGet("/documents/{id}/text", async (string id, HttpContext http, CancellationToken ct) =>
        {
            if (!TryResolve<IDocumentCatalog>(http, out var catalog, out var unavailable)) return unavailable!;
            if (!TryResolve<IDocumentTextReader>(http, out var reader, out unavailable)) return unavailable!;

            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            if (detail.BlobPath is null) return Results.Conflict(new { reason = detail.UnavailableReason });

            // An empty list is the honest answer for a document that reached bronze but never the
            // index — it means no agent has ever read it, and the viewer says so.
            return Results.Json(await reader.ReadChunksAsync(detail, ct), Json.Options);
        });
    }

    // The FACETS the list endpoint filters on — not DocumentId's kinds. `sdsgap` is deliberately
    // absent: a missing sheet is still a sheet, and only the id scheme draws that distinction.
    private static readonly string[] KnownKinds =
        [DocumentKinds.All, DocumentKinds.Sds, DocumentKinds.Reg, DocumentKinds.Seed, DocumentKinds.Coa];

    private static readonly string[] KnownStates =
        [DocumentStates.All, DocumentStates.Available, DocumentStates.Missing, DocumentStates.Superseded];

    /// The offending value, or null when the filter is absent, empty (meaning "unset") or known.
    private static string? Unknown(string? value, string[] allowed) =>
        value is { Length: > 0 } && !allowed.Contains(value, StringComparer.Ordinal) ? value : null;

    /// Resolves a document service, turning both configuration faults into a 503 that says which.
    /// GetService rather than GetRequiredService, so an unregistered service and a factory that threw
    /// are told apart and reported differently — see the comment on the list route.
    private static bool TryResolve<T>(HttpContext http, out T service, out IResult? unavailable)
        where T : class
    {
        try
        {
            if (http.RequestServices.GetService<T>() is { } resolved)
            {
                (service, unavailable) = (resolved, null);
                return true;
            }
            service = null!;
            unavailable = Unavailable(
                $"The document library is not configured on this deployment: {typeof(T).Name} is not " +
                "registered, which means COSMOS_ACCOUNT_ENDPOINT is unset.");
        }
        catch (InvalidOperationException e)
        {
            // The message is ours (Program.cs names the missing variable) and this is a
            // single-operator internal tool, so it is surfaced rather than swallowed — spec §8.
            service = null!;
            unavailable = Unavailable(e.Message);
        }
        return false;
    }

    private static IResult Unavailable(string detail) => Results.Problem(
        title: "Document library unavailable", detail: detail,
        statusCode: StatusCodes.Status503ServiceUnavailable);

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
