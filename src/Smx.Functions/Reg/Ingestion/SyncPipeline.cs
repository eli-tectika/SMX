using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Sds.Ingestion; // reuse IEmbedder

namespace Smx.Functions.Reg.Ingestion;

// The Regulatory Sync pipeline — the SDS-style testable core (RunSyncAsync), no Durable. A monthly timer calls
// RunSyncAsync; per document it fetches → sha256 change-detects → writes Bronze → parses → stages Silver. It
// then computes the corpus diff and, via the circuit breaker, either promotes automatically (normal path) or
// holds for R.E. sign-off (anomaly path; resumed later by ReviewDecisionHttp). Promotion embeds the staged
// chunks and pushes them to the Gold index, advances change-detection state, and flips Silver staged→live.
public sealed class SyncPipeline
{
    private readonly RegRegistryProvider _registry;
    private readonly IRegEgress _egress;
    private readonly BronzeIngestor _bronze;
    private readonly RegParserRegistry _parsers;
    private readonly IRegSilverStore _silver;
    private readonly IRegStateStore _state;
    private readonly IRegReviewStore _review;
    private readonly IRegRunsStore _runs;
    private readonly IEmbedder _embedder;
    private readonly IRegSearchClient _search;
    private readonly RegOptions _opts;
    private readonly ILogger<SyncPipeline> _log;

    public SyncPipeline(RegRegistryProvider registry, IRegEgress egress, BronzeIngestor bronze,
        RegParserRegistry parsers, IRegSilverStore silver, IRegStateStore state, IRegReviewStore review,
        IRegRunsStore runs, IEmbedder embedder, IRegSearchClient search, RegOptions opts, ILogger<SyncPipeline> log)
    {
        _registry = registry; _egress = egress; _bronze = bronze; _parsers = parsers; _silver = silver;
        _state = state; _review = review; _runs = runs; _embedder = embedder; _search = search;
        _opts = opts; _log = log;
    }

    // Testable core (no trigger attribute), mirroring SdsSweep.RunSweepAsync. `nowUtc` is an ISO-8601 instant.
    public async Task<CorpusDiff> RunSyncAsync(string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc);
        await ExpireStaleHeldAsync(now, ct);
        var runId = $"sync-{now:yyyyMM}";
        var fetchTs = now.ToString("yyyyMMddTHHmmssZ");
        var syncDate = now.ToString("yyyy-MM-dd");
        var outcomes = new List<DocOutcome>();
        var errorDetails = new List<string>();

        // The timer sweep is monthly: process only monthly-cadence sources (a null cadence defaults to
        // monthly for back-compat). Quarterly/static sources are skipped here and swept on their own schedule.
        foreach (var source in _registry.Enabled.Where(s => s.Cadence is null || s.Cadence == "monthly"))
        foreach (var doc in source.Documents)
        {
            try
            {
                var b = await _bronze.FetchAndStageAsync(source, doc, _egress, runId, fetchTs, ct);
                if (b.Result is DocResult.Unchanged)
                { outcomes.Add(new DocOutcome(source.SourceId, doc.DocId, b.Result, 0, null)); continue; }
                if (b.Result is DocResult.Error)
                {
                    errorDetails.Add($"{source.SourceId}/{doc.DocId}: fetch failed");
                    outcomes.Add(new DocOutcome(source.SourceId, doc.DocId, DocResult.Error, 0, "fetch failed"));
                    continue;
                }

                var parsed = _parsers.Get(source.Parser).Parse(b.Raw!, source, doc);
                var chunks = SilverBuilder.Build(source, doc, b.Sha256!, runId, syncDate, parsed);
                await _silver.UpsertStagedAsync(chunks, ct);
                outcomes.Add(new DocOutcome(source.SourceId, doc.DocId, b.Result, chunks.Count, null));
                _log.LogInformation("Reg staged {Count} chunks for {Source}/{Doc} ({Result})",
                    chunks.Count, source.SourceId, doc.DocId, b.Result);
            }
            catch (Exception ex) // per-document isolation: one bad source does not fail the run
            {
                _log.LogError(ex, "Reg ingest failed for {Source}/{Doc}", source.SourceId, doc.DocId);
                errorDetails.Add($"{source.SourceId}/{doc.DocId}: {ex.Message}");
                outcomes.Add(new DocOutcome(source.SourceId, doc.DocId, DocResult.Error, 0, ex.Message));
            }
        }

        var diff = DiffEngine.Compute(runId, outcomes, _opts);
        var startedUtc = nowUtc;

        if (diff.ChangedDocIds.Count == 0)
        {
            await _review.UpsertAsync(new ReviewRecord(runId, runId, diff, RegStatus.AutoPromoted, "auto", null, nowUtc, null), ct);
            await _runs.UpsertAsync(new SyncRun(runId, runId, "no-op", diff.Added, diff.Changed, diff.Unchanged, diff.Errors, startedUtc, nowUtc, errorDetails), ct);
            _log.LogInformation("Reg sync {RunId}: no changes", runId);
            return diff;
        }

        if (diff.Anomaly.Anomalous)
        {
            // Circuit breaker: hold for human review. Gold is NOT promoted until ReviewDecisionHttp approves.
            await _review.UpsertAsync(new ReviewRecord(runId, runId, diff, RegStatus.Held, null, null, nowUtc, null), ct);
            await _runs.UpsertAsync(new SyncRun(runId, runId, RegStatus.Held, diff.Added, diff.Changed, diff.Unchanged, diff.Errors, startedUtc, nowUtc, errorDetails), ct);
            _log.LogWarning("Reg sync {RunId} HELD for review: {Reasons}", runId, string.Join("; ", diff.Anomaly.Reasons));
            return diff;
        }

        await PromoteAsync(runId, ct);
        await _review.UpsertAsync(new ReviewRecord(runId, runId, diff, RegStatus.AutoPromoted, "auto", null, nowUtc, null), ct);
        await _runs.UpsertAsync(new SyncRun(runId, runId, RegStatus.AutoPromoted, diff.Added, diff.Changed, diff.Unchanged, diff.Errors, startedUtc, nowUtc, errorDetails), ct);
        _log.LogInformation("Reg sync {RunId} auto-promoted {Docs} changed doc(s)", runId, diff.ChangedDocIds.Count);
        return diff;
    }

    // Promote a run's staged Silver to the live Gold index. Self-contained by runId so it serves both the auto
    // path and the ReviewDecisionHttp approve path. Embeds staged chunk text, pushes to AI Search, advances
    // change-detection state per doc, then flips Silver staged→live (superseding prior live chunks).
    public async Task PromoteAsync(string runId, CancellationToken ct)
    {
        var staged = await _silver.GetStagedAsync(runId, ct);
        if (staged.Count == 0) { _log.LogWarning("Reg promote {RunId}: no staged chunks", runId); return; }

        var vectors = await _embedder.EmbedAsync(staged.Select(c => c.Text).ToList(), ct);
        var gold = new List<GoldChunk>(staged.Count);
        for (var i = 0; i < staged.Count; i++)
        {
            var c = staged[i];
            gold.Add(new GoldChunk(c.Id, c.Text, vectors[i], c.Citation.Regulation, c.Citation.Authority,
                c.SourceId, c.Citation.EntryId, c.DocId, c.Citation.SourceUrl, c.Citation.OfficialDate, c.SyncDate));
        }
        await _search.EnsureIndexAsync(ct);
        await _search.PushAsync(gold, ct);

        // Advance per-doc change-detection state from the staged chunks (docId → sha/officialDate/syncDate).
        foreach (var g in staged.GroupBy(c => c.DocId))
        {
            var head = g.First();
            await _state.UpsertAsync(new RegDocState(
                head.DocId, head.SourceId, head.DocSha256, head.Citation.OfficialDate, runId, head.SyncDate), ct);
        }

        var changedDocIds = staged.Select(c => c.DocId).Distinct().ToList();
        await _silver.PromoteStagedToLiveAsync(runId, changedDocIds, ct);
    }

    // Expire held runs that were never signed off within the configured window: discard their staged Silver and
    // mark them held-expired. Prevents an abandoned anomaly from pinning stale staged chunks indefinitely.
    public async Task ExpireStaleHeldAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_opts.HeldExpiryDays);
        foreach (var held in await _review.GetByStatusAsync(RegStatus.Held, ct))
        {
            if (!DateTimeOffset.TryParse(held.CreatedUtc, out var created) || created > cutoff) continue;
            await _silver.DiscardStagedAsync(held.SyncRunId, ct);
            await _review.UpsertAsync(held with { Status = RegStatus.HeldExpired }, ct);
            _log.LogWarning("Reg review {RunId} held-expired after {Days}d without sign-off", held.SyncRunId, _opts.HeldExpiryDays);
        }
    }
}
