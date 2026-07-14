using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Providers;

namespace Smx.SearchProxy.Pipeline;

public sealed record PipelineResult(SearchResponse? Response, int StatusCode, string? Reason);

/// The testable core. The trigger is a shell over this (house convention: SdsSweep.RunSweepAsync,
/// SyncPipeline.RunSyncAsync). `nowUtc` is a parameter so cache TTL and quota windows are deterministic.
///
/// Order matters and is not arbitrary:
///   guard  → nothing that should never egress can reach the quota, the cache key, or the provider
///   quota  → a runaway loop is stopped BEFORE the provider is called, not after the bill
///   cache  → a hit egresses nothing at all, which is the safest possible outcome
///   cover  → and only now, wrapped in decoys, does anything leave the building
public sealed class SearchPipeline(
    StructuralGuard guard,
    QuotaGuard quota,
    ISearchCache cache,
    CoverBatch cover,
    ISearchProvider provider,
    EgressAudit audit,
    ProxyOptions opts,
    ILogger<SearchPipeline> log)
{
    public async Task<PipelineResult> RunAsync(SearchRequest req, string nowUtc, CancellationToken ct)
    {
        var verdict = guard.Check(req);
        if (!verdict.Allowed)
        {
            audit.Blocked(req, verdict.Reason!);
            return new PipelineResult(null, 400, verdict.Reason);
        }

        if (!opts.DryRun && string.IsNullOrEmpty(opts.ApiKey))
        {
            audit.Blocked(req, "provider_not_configured");
            return new PipelineResult(null, 503, "provider_not_configured");
        }

        var key = CacheKey.For(req.Query, req.Intent, req.MaxResults);
        var cached = await cache.GetAsync(key, nowUtc, ct);
        if (cached is not null)
        {
            audit.CacheHit(req, cached.Count);
            return new PipelineResult(new SearchResponse(cached, cached.Count, CacheHit: true, CoverCount: 0), 200, null);
        }

        var batch = cover.Build(req.Query, req.Intent);

        // Charge the WHOLE batch, decoys included: that is what the provider bills and what actually egresses.
        if (!await quota.TryConsumeAsync(batch.Count, nowUtc, ct))
        {
            audit.Blocked(req, "quota_exceeded");
            return new PipelineResult(null, 429, "quota_exceeded");
        }

        // Concurrently — a serialized batch would leak an ordering signal, and the real query would sit at a
        // predictable position in time. Fired together, the N queries are indistinguishable by timing.
        var answers = await Task.WhenAll(batch.Select(async q =>
            (Query: q, Hits: await provider.SearchAsync(q, req.MaxResults, req.FreshnessDays, ct))));

        foreach (var (query, hits) in answers)
        {
            if (hits is null) continue;
            await cache.SetAsync(CacheKey.For(query, req.Intent, req.MaxResults), hits, nowUtc, ct);
        }

        var real = answers.First(a => a.Query == req.Query).Hits;
        if (real is null)
        {
            // NOT an empty result set. An agent reading [] would conclude "no such marker exists" and act on
            // it; a 502 tells it the question was never answered.
            log.LogWarning("Provider failed for the real query; {Decoys} decoys were issued regardless", batch.Count - 1);
            audit.ProviderFailed(req, batch.Count);
            return new PipelineResult(null, 502, "provider_failed");
        }

        audit.Allowed(req, batch.Count, real.Count);
        return new PipelineResult(new SearchResponse(real, real.Count, CacheHit: false, CoverCount: batch.Count), 200, null);
    }
}
