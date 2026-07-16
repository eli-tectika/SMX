using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The offline round-trip artifacts (§7: "the system generates what the operator takes offline") — what
/// the operator hands the R.E. Both are DETERMINISTIC projections over the record (like the xlsx export):
/// no agent touches them, so what the R.E. reviews is exactly what the record says, every time. The return
/// inbox is the existing operator-entry endpoints (/regulatory/review, /regulatory/determination).
///
/// JSON for now; xlsx is a deferred follow-on until the operator asks (the R.E.'s actual offline workflow
/// may reshape these first-cut shapes — spec open item §13).
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see ProjectEndpoints:12-16.
        //
        // elements-to-check: one item per DISTINCT candidate substance still in the analysis (non-Tier-C),
        // components and markets folded. Coverage is the contract: a substance dropped here is a substance
        // the R.E. never audits — an unreviewed substance that LOOKS reviewed the moment the gate signs.
        // Tier-C is excluded deliberately and completely: those candidates are out of the analysis, and the
        // R.E.'s time is the budget — auditing dead candidates spends it.
        app.MapGet("/projects/{projectId}/regulatory/elements-to-check",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var candidates = await store.GetCandidatesAsync(projectId, ct);
            if (candidates is null) return Results.NotFound();

            var constraints = await store.GetConstraintsAsync(projectId, ct);
            var marketsByComponent = (constraints?.Components ?? [])
                .ToDictionary(c => c.Id, c => c.Markets);

            var items = candidates.Substances
                .Where(s => s.Tier != "C")                 // the same live-set rule as MatrixAssembler.Cells
                .GroupBy(s => s.Cas)
                .Select(g =>
                {
                    var components = g.Select(s => s.ComponentId).Distinct().ToList();
                    return new
                    {
                        g.First().Element,
                        cas = g.Key,
                        g.First().Form,
                        components,
                        markets = components
                            .SelectMany(id => marketsByComponent.TryGetValue(id, out var m) ? m : [])
                            .Distinct().ToList(),
                    };
                })
                .ToList();

            return Results.Json(new
            {
                projectId,
                generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                items,
            }, Json.Options);
        });

        // compliance-package: one entry per verdict, citations passed through VERBATIM — the R.E. checks
        // the sources, and an entry whose citations went missing is unreviewable. A dimension with no
        // citations rides through carrying its honest empty state (citations: []), never dropped: a
        // dimension that vanished from the package looks like a dimension that was never screened.
        app.MapGet("/projects/{projectId}/regulatory/compliance-package",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            // No verdicts ⇒ no package. Unlike GET /verdicts (where [] is a state), an EMPTY package
            // handed to the R.E. is the degenerate narrowed review: zero entries posing as a screening.
            if (verdicts.Count == 0) return Results.NotFound();

            var entries = verdicts.Select(v => new
            {
                v.Cas,
                v.ComponentId,
                v.Element,
                v.Overall,
                dimensions = v.Dimensions.Select(d => new
                {
                    d.Dimension,
                    d.Status,
                    d.Confidence,
                    d.Rationale,
                    d.Citations,   // VERBATIM pass-through — the record's citations, not a summary of them
                }).ToList(),
                v.ProposedDetermination,
                v.ProposedReason,
            }).ToList();

            return Results.Json(new
            {
                projectId,
                generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                corpusSyncNote = "citations carry the regulatory corpus sync date in retrievedAt — verify "
                    + "currency against the monthly Regulatory Sync before relying on a clearance",
                entries,
            }, Json.Options);
        });
    }
}
