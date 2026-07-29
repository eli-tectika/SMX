using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Documents;
using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Backend.Api;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative: without it, minimal APIs infer whether `store`
        // is a service by checking IServiceProviderIsService at endpoint-build time. Test hosts that
        // don't register IKnowledgeStore (e.g. ProjectEndpointsTests, which only registers IRecordStore)
        // would then have it inferred as a request body, which is illegal on GET and throws while
        // building the composite endpoint data source — breaking routing for the ENTIRE app, not just
        // these routes.
        app.MapGet("/marker-library", async (string? search, [FromServices] IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryMarkersAsync(search, ct), Json.Options));

        app.MapGet("/learned-conclusions", async (string? search, [FromServices] IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryLearnedConclusionsAsync(search, ct), Json.Options));

        // Compose-at-read (design §6.3): the SDS corpus (`sds-registry`, written by the SDS library
        // subsystem) is the source of sheet FACTS; `msds-registry` holds only the governance overlay.
        // A plain in-memory merge by CAS — no duplication, no sync machinery, and no LLM anywhere near
        // a gate-backing surface.
        //
        // The overlay's only remaining job is LinkedProjects (D8 deleted the review signature), which
        // simplifies the merge in a way worth naming: the old code applied the overlay only when its
        // `Date` matched the current revision, so that a signature over an older sheet could not
        // silently bless a newer one. LinkedProjects carries no such hazard — "project P put this
        // substance in play" does not stop being true when a fresher revision arrives — so the merge is
        // now by CAS alone, and sheet facts always come from the sheet.
        app.MapGet("/msds-registry", async (string? search, [FromServices] IKnowledgeStore store,
            [FromServices] ISdsCorpusReader corpus, CancellationToken ct) =>
        {
            var governance = await store.QueryMsdsAsync(search, ct);
            var sheets = await corpus.QuerySheetsAsync(search, ct);

            // Latest current sheet per CAS (ISO date strings — ordinal order is chronological).
            var latest = sheets
                .GroupBy(s => s.Cas, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.RevisionDate, StringComparer.Ordinal)
                              .ThenByDescending(s => s.IngestedUtc, StringComparer.Ordinal).First())
                .ToList();

            var overlay = governance.ToDictionary(g => g.Cas, StringComparer.OrdinalIgnoreCase);
            var rows = new List<MsdsRegistryRow>();
            foreach (var s in latest)
            {
                overlay.Remove(s.Cas, out var g);
                rows.Add(new MsdsRegistryRow(
                    KnowledgeIds.Msds(s.Cas), KnowledgeTypes.MsdsRegistry, s.Cas, s.Supplier,
                    // The corpus records no version number; don't invent one.
                    Version: "", Date: s.RevisionDate,
                    LinkedProjects: g?.LinkedProjects ?? [],
                    DocumentId: SheetDocumentId(s)));
            }
            // Governance-only rows (manual/legacy) stay visible, but carry no document id: there is
            // no corpus sheet behind them, so there is nothing to open. They are also, now, exactly the
            // rows the order gate refuses — a governance record is not a safety sheet.
            rows.AddRange(overlay.Values.Select(g => MsdsRegistryRow.From(g, null)));
            return Results.Json(rows.OrderBy(r => r.Cas, StringComparer.Ordinal), Json.Options);
        });

        // Fetch the sheet for a CAS, now, on the operator's word.
        //
        // The passthrough that makes "Fetch now" possible. Acquisition itself lives in the `regsync`
        // Function App (D6 — it holds the only NAT'd subnet and the corpus write grants); this is the
        // line to it, and it is deliberately thin: the result is returned verbatim, `attempted[]` and
        // all, because when the answer is "unavailable" the operator is owed what was tried and why
        // each candidate failed — not merely that it did not work.
        //
        // 200 even for `unavailable`: the request succeeded and produced a truthful answer about the
        // world. A 4xx/5xx here would be a claim about THIS endpoint, and would drag the browser onto
        // an error path that discards the very diagnostics the call exists to deliver.
        app.MapPost("/msds/{cas}/fetch", async (string cas, [FromServices] ISdsAcquisition sds,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(cas)) return Results.BadRequest(new { error = "a CAS is required" });
            var result = await sds.EnsureAsync(cas.Trim(), element: null, form: null, ct);
            return Results.Json(result, Json.Options);
        });

        // The fallback that has never existed: hand the system a sheet it could not find.
        //
        // No gate sits behind this. It is not an override either — the file goes through the same
        // content validation every fetched sheet faces, so a document that is not a safety sheet for
        // this substance does not become one by arriving through a file picker.
        //
        // Supplier and revision date are REQUIRED, and that is not form-filling zeal: together with the
        // CAS they are the registry's compound key (DedupKey.ForRegistry). A blank one mints an id with
        // an empty segment, which DocumentId.TryDecode refuses forever after — the sheet would be
        // ingested, listed, and permanently un-openable. Refusing here costs the operator two fields;
        // accepting it costs them the document.
        app.MapPost("/msds/{cas}/upload", async (string cas, HttpRequest request,
            [FromServices] ISdsAcquisition sds, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "expected a multipart form" });
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.UnprocessableEntity(new { error = "no file was uploaded" });

            var supplier = (form["supplier"].ToString() ?? "").Trim();
            var revisionDate = (form["revisionDate"].ToString() ?? "").Trim();
            if (supplier.Length == 0 || revisionDate.Length == 0)
                return Results.UnprocessableEntity(new
                {
                    error = "a supplier and a revision date are required — they are part of the sheet's " +
                            "identity, and a sheet stored without them cannot be opened again",
                });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            // The product name is a label, not a key. Falling back to the CAS keeps the row nameable
            // without asking the operator for a third field the registry does not depend on.
            var productName = (form["productName"].ToString() ?? "").Trim();
            var result = await sds.UploadAsync(new SdsUpload(
                cas.Trim(), supplier, productName.Length > 0 ? productName : cas.Trim(),
                revisionDate, ms.ToArray()), ct);

            // Unlike the fetch passthrough, a rejected upload IS a 422: the operator handed us a file and
            // the answer is that this file will not do. That belongs on the browser's failure path.
            return result.Ok ? Results.Json(result, Json.Options) : Results.UnprocessableEntity(result);
        }).DisableAntiforgery();
    }

    /// The document id of the sheet a registry row was composed from, or null if it would not
    /// resolve.
    ///
    /// Served rather than derived by the caller. The registry screen has all three parts of the id
    /// in front of it, so the browser *could* build one — but that would put DedupKey's
    /// normalisation in a second language, where the two copies drifting apart shows up only as a
    /// silent 404 on the screen that blocks procurement. SdsRegistryKey is the one mirror of that
    /// rule, and it is guarded by a test that compiles the real DedupKey.
    ///
    /// CanEncode, not Encode: a corpus row with a blank supplier or revision date makes a payload
    /// with an empty segment, which DocumentId.TryDecode refuses. A link that 404s is worse than no
    /// link — the row is still listed either way, which is what the registry is for.
    private static string? SheetDocumentId(SdsCorpusSheet s)
    {
        var payload = SdsRegistryKey.ForRegistry(s.Cas, s.Supplier, s.RevisionDate);
        return DocumentId.CanEncode(DocumentId.Sds, payload)
            ? DocumentId.Encode(DocumentId.Sds, payload)
            : null;
    }
}

/// One MSDS registry row on the wire: the governance document, plus the one thing it does not and
/// should not store — the id of the corpus sheet behind it. Derived at read time, exactly like the
/// rest of this composition (design §6.3: reference the corpus, never duplicate it), so persisting
/// it on MsdsRegistryDoc would create a second copy that can go stale when a newer sheet arrives.
public sealed record MsdsRegistryRow(
    string Id,
    string Type,
    string Cas,
    string Supplier,
    string Version,
    string Date,
    IReadOnlyList<string> LinkedProjects,
    string? DocumentId)
{
    public static MsdsRegistryRow From(MsdsRegistryDoc d, string? documentId) => new(
        d.Id, d.Type, d.Cas, d.Supplier, d.Version, d.Date, d.LinkedProjects, documentId);
}
