using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

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
        // subsystem) is the source of sheet FACTS; `msds-registry` holds only the governance overlay
        // (the operator's review signature). A plain in-memory merge by CAS — no duplication, no sync
        // machinery, and no LLM anywhere near a gate-backing surface.
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
            var rows = new List<MsdsRegistryDoc>();
            foreach (var s in latest)
            {
                // The overlay applies only if the signature names THIS revision: a review of an
                // older sheet must never silently bless a newer one.
                if (overlay.Remove(s.Cas, out var g) && g.Date == s.RevisionDate)
                    rows.Add(g);
                else
                    rows.Add(new MsdsRegistryDoc
                    {
                        Id = KnowledgeIds.Msds(s.Cas), Cas = s.Cas, Supplier = s.Supplier,
                        Version = "", Date = s.RevisionDate,   // the corpus records no version number; don't invent one
                    });
            }
            rows.AddRange(overlay.Values);   // governance-only rows (manual/legacy) stay visible
            return Results.Json(rows.OrderBy(r => r.Cas, StringComparer.Ordinal), Json.Options);
        });

        app.MapPost("/msds-registry/{cas}/review", async (string cas, [FromServices] IKnowledgeStore store,
            [FromServices] ISdsCorpusReader corpus, CancellationToken ct) =>
        {
            // Reviewing signs the CURRENT sheet. If no governance doc exists yet, create it from the
            // corpus; if neither exists there is nothing to sign — a signature must reference a real sheet.
            var sheet = await corpus.GetLatestForCasAsync(cas, ct);
            var m = await store.GetMsdsAsync(cas, ct);
            if (m is null && sheet is null)
                return Results.NotFound();
            m ??= new MsdsRegistryDoc
            {
                Id = KnowledgeIds.Msds(cas), Cas = cas, Supplier = sheet!.Supplier,
                Version = "", Date = sheet.RevisionDate,
            };
            if (sheet is not null) { m.Supplier = sheet.Supplier; m.Date = sheet.RevisionDate; }
            m.ReviewStatus = MsdsReviewStatus.Reviewed;
            // The MSDS review is a signed record, not a flag: the MSDS-before-order hard gate (Plan 5)
            // reads it, so when it was signed must be recoverable.
            m.ReviewedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertMsdsAsync(m, ct);
            return Results.Ok(new { m.Cas, m.ReviewStatus, m.ReviewedAt });
        });
    }
}
