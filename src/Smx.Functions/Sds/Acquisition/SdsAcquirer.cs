using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Acquisition;

public static class EnsureStatus
{
    public const string AlreadyHad = "already-had";
    public const string Fetched = "fetched";
    public const string Unavailable = "unavailable";
}

public sealed record AttemptRecord(string Url, string Supplier, string Outcome);

public sealed record EnsureResult(
    string Status, string? RegistryId, string? Supplier, string? RevisionDate,
    string? Reason, IReadOnlyList<AttemptRecord> Attempted);

/// Acquiring one substance's safety sheet, start to finish. THE code path — the timer, the operator's
/// manual sync and an agent's `ensure_sds` all run this, so there is exactly one place where the rules
/// about what counts as a sheet and what happens when there isn't one are written down.
///
/// Before 2026-07-29 this logic lived inside the sweep loop and was reachable only on a weekly schedule.
/// An agent that discovered it lacked a hazard sheet had to park its stage and wait days. Nothing here
/// blocks, and nothing here is terminal: an unobtainable sheet returns `unavailable` with the attempts
/// recorded, and the entry keeps its place in the backoff queue.
public sealed class SdsAcquirer
{
    private readonly MasterListRepo _masterList;
    private readonly RegistryRepo _registry;
    private readonly SourceResolver _resolver;
    private readonly IEgressClient _egress;
    private readonly IngestionPipeline _pipeline;
    private readonly SdsOptions _opts;
    private readonly ILogger<SdsAcquirer> _log;

    public SdsAcquirer(MasterListRepo masterList, RegistryRepo registry, SourceResolver resolver,
        IEgressClient egress, IngestionPipeline pipeline, SdsOptions opts, ILogger<SdsAcquirer> log)
    { _masterList = masterList; _registry = registry; _resolver = resolver; _egress = egress;
      _pipeline = pipeline; _opts = opts; _log = log; }

    /// <param name="force">
    /// Skip the cache short-circuit and re-fetch. The sweep passes true: it has ALREADY decided the entry
    /// is due, and a `fetched` row coming up for its revision recheck would otherwise short-circuit on the
    /// very sheet the recheck exists to replace.
    /// </param>
    public async Task<EnsureResult> EnsureAsync(
        SubstanceKey key, bool force, string nowUtc, CancellationToken ct)
    {
        // Checked here rather than left to whichever downstream call happens to notice: a caller that has
        // already gone away must not cause a fetch, and "did we stop?" should not depend on whether the
        // store or the HTTP stack got around to looking at the token.
        ct.ThrowIfCancellationRequested();

        var attempted = new List<AttemptRecord>();

        // 1. Cache hit. Deliberately free — no resolver call, no egress, no search spend. This is what
        //    makes ensure_sds safe for an agent to call whenever it is unsure.
        if (!force)
        {
            var existing = await _registry.GetForSubstanceAsync(key.Cas, null, ct);
            if (existing is { Indexed: true })
                return new EnsureResult(EnsureStatus.AlreadyHad, existing.Id, existing.Supplier,
                    existing.RevisionDate, null, attempted);
        }

        // 2. The ledger fills itself. A substance somebody asked for is a substance we need a sheet for,
        //    so even a failed ensure leaves the sweep knowing to keep trying.
        var entry = await EnsureLedgerEntryAsync(key, nowUtc, ct);

        // 3. One budget for the whole attempt. An agent is waiting on this call, and a supplier that
        //    trickles bytes must not be able to hold a stage open indefinitely.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_opts.EnsureBudgetSeconds));

        try
        {
            return await AttemptAsync(key, entry, attempted, nowUtc, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The budget expired, not the caller. That is a real answer — record the failure so the
            // entry backs off, and tell the caller what we got through before the clock ran out.
            _log.LogWarning("Ensure for {Cas} exceeded its {Budget}s budget", key.Cas, _opts.EnsureBudgetSeconds);
            if (entry is not null) await _masterList.RecordFailureAsync(entry, nowUtc, CancellationToken.None);
            return new EnsureResult(EnsureStatus.Unavailable, null, null, null,
                $"gave up after {_opts.EnsureBudgetSeconds}s", attempted);
        }
    }

    private async Task<EnsureResult> AttemptAsync(
        SubstanceKey key, MasterListEntry? entry, List<AttemptRecord> attempted, string nowUtc, CancellationToken ct)
    {
        EgressFetch fetch = (url, c) => _egress.FetchAsync(url, c);

        IReadOnlyList<SourceCandidate> candidates;
        try
        {
            candidates = await _resolver.ResolveAsync(key, fetch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A resolver blow-up is one substance's problem. It must not escape into a sweep of 53.
            _log.LogWarning(ex, "Resolving sources for {Cas} threw", key.Cas);
            if (entry is not null) await _masterList.RecordFailureAsync(entry, nowUtc, CancellationToken.None);
            return new EnsureResult(EnsureStatus.Unavailable, null, null, null,
                $"could not work out where to look: {ex.Message}", attempted);
        }

        foreach (var candidate in candidates)
        {
            // Between candidates as well as before the first: this is where the budget actually bites,
            // since a supplier list is walked one slow fetch at a time.
            ct.ThrowIfCancellationRequested();
            try
            {
                var fetched = await _egress.FetchAsync(candidate.Url, ct);
                if (fetched is null)
                {
                    attempted.Add(new(candidate.Url.ToString(), candidate.Supplier, "no response"));
                    continue;
                }

                // RevisionDate is stamped with the fetch date, not the document's own revision date —
                // a known, pre-existing gap (see the 2026-07-29 spec, out of scope).
                var meta = new SdsMetadata(key.Cas, candidate.Supplier, key.Form, nowUtc[..10],
                    null, null, candidate.Url.ToString(), entry?.Id ?? "", candidate.Strategy);

                var result = await _pipeline.IngestAsync(fetched.Content, meta, ct);
                if (result.Ok)
                {
                    if (entry is not null) await _masterList.MarkFetchedAsync(entry, nowUtc, ct);
                    return new EnsureResult(EnsureStatus.Fetched, result.RegistryId, candidate.Supplier,
                        nowUtc[..10], null, attempted);
                }

                attempted.Add(new(candidate.Url.ToString(), candidate.Supplier, $"rejected: {result.Reason}"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                attempted.Add(new(candidate.Url.ToString(), candidate.Supplier, $"threw: {ex.Message}"));
                _log.LogWarning(ex, "Candidate {Url} threw; trying next supplier", candidate.Url);
            }
        }

        if (entry is not null) await _masterList.RecordFailureAsync(entry, nowUtc, ct);

        // Naming what was tried is part of the contract. "Unavailable" alone would leave an agent unable
        // to tell a bot-walled supplier from a substance that has no published sheet at all.
        var reason = candidates.Count == 0
            ? "no source could be found for this substance"
            : $"none of the {candidates.Count} candidate source(s) yielded a valid sheet";
        return new EnsureResult(EnsureStatus.Unavailable, null, null, null, reason, attempted);
    }

    /// The ledger row this substance belongs to, creating it if it is new.
    ///
    /// Returns null only when the caller gave a CAS and nothing else and no existing row matches it: the
    /// row id is derived from element+form, so there is nothing to key on. The fetch still proceeds —
    /// answering the question is more useful than refusing over bookkeeping — it simply goes unrecorded.
    private async Task<MasterListEntry?> EnsureLedgerEntryAsync(SubstanceKey key, string nowUtc, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key.Element) && !string.IsNullOrWhiteSpace(key.Form))
        {
            await _masterList.AppendAsync(key.Element, key.Form, key.Cas, null, "ensure", nowUtc, ct);
            return await _masterList.GetAsync(key.Element, key.Form, ct);
        }
        return await _masterList.FindByCasAsync(key.Cas, ct);
    }
}
