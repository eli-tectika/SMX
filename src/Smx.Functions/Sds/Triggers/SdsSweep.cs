using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Triggers;

public sealed class SdsSweep
{
    private readonly MasterListRepo _masterList;
    private readonly SourceResolver _resolver;
    private readonly IEgressClient _egress;     // sole holder of the egress client
    private readonly IngestionPipeline _pipeline;
    private readonly SdsOptions _opts;
    private readonly ILogger<SdsSweep> _log;

    public SdsSweep(MasterListRepo masterList, SourceResolver resolver, IEgressClient egress,
        IngestionPipeline pipeline, SdsOptions opts, ILogger<SdsSweep> log)
    { _masterList = masterList; _resolver = resolver; _egress = egress; _pipeline = pipeline; _opts = opts; _log = log; }

    [Function("SdsSweep")]
    public Task Run([TimerTrigger("%SDS_SWEEP_CRON%")] TimerInfo timer, CancellationToken ct)
        => RunSweepAsync(DateTimeOffset.UtcNow.ToString("O"), ct);

    // Testable core (no trigger attribute): process the whole due set in bulk.
    public async Task RunSweepAsync(string nowUtc, CancellationToken ct)
    {
        var due = await _masterList.QueryDueAsync(_opts.RetryCap, _opts.RevisionRecheckDays, nowUtc, ct);
        _log.LogInformation("SDS sweep: {Count} due entries", due.Count);

        EgressFetch fetch = (url, c) => _egress.FetchAsync(url, c);

        // Batch robustness: a single bad supplier response or resolver blow-up must cost only that
        // candidate/entry, never the rest of the bulk run — the bulk-and-scheduled property is the
        // leak mitigation, so the batch must always complete. Cancellation still aborts the sweep.
        foreach (var entry in due)
        {
            try
            {
                var key = new SubstanceKey(entry.Element, entry.Form, entry.Cas);
                var candidates = await _resolver.ResolveAsync(key, fetch, ct);
                var ingested = false;

                foreach (var candidate in candidates)
                {
                    try
                    {
                        var fetched = await _egress.FetchAsync(candidate.Url, ct);
                        if (fetched is null) continue;

                        var meta = new SdsMetadata(entry.Cas, candidate.Supplier, entry.Form, nowUtc[..10],
                            null, null, candidate.Url.ToString(), entry.Id);
                        var result = await _pipeline.IngestAsync(fetched.Content, meta, candidate.Domain, ct);
                        if (result.Ok) { ingested = true; break; }
                        _log.LogInformation("Candidate {Url} rejected: {Reason}", candidate.Url, result.Reason);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Candidate {Url} threw; trying next supplier", candidate.Url);
                    }
                }

                if (ingested) await _masterList.MarkFetchedAsync(entry, nowUtc, ct);
                else await _masterList.RecordFailureAsync(entry, _opts.RetryCap, nowUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sweep entry {Id} threw; recording failure and continuing", entry.Id);
                await _masterList.RecordFailureAsync(entry, _opts.RetryCap, nowUtc, ct);
            }
        }
    }
}
