using System.Globalization;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class MasterListRepo
{
    private readonly IMasterListStore _store;
    public MasterListRepo(IMasterListStore store) => _store = store;

    public async Task<bool> AppendAsync(string element, string form, string cas, string? substrateClass,
        string addedBy, string nowUtc, CancellationToken ct)
    {
        var id = DedupKey.ForMasterList(element, form);
        if (await _store.GetAsync(id, element, ct) is not null) return false;
        await _store.UpsertAsync(new MasterListEntry(
            id, element, form, cas, substrateClass, SdsStatus.Pending, addedBy, nowUtc, null, 0), ct);
        return true;
    }

    public Task<MasterListEntry?> GetAsync(string element, string form, CancellationToken ct)
        => _store.GetAsync(DedupKey.ForMasterList(element, form), element, ct);

    /// For a caller that has only a CAS. The row id is derived from element+form, so there is no key to
    /// read by — this scans. That is affordable because the list is small (53 rows on 2026-07-29) and the
    /// alternative is refusing to fetch a sheet somebody asked for by the one identifier they had.
    public async Task<MasterListEntry?> FindByCasAsync(string cas, CancellationToken ct)
        => (await _store.ListAllAsync(ct))
            .FirstOrDefault(e => string.Equals(e.Cas, cas.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<MasterListEntry>> QueryDueAsync(
        int recheckDays, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        var all = await _store.ListAllAsync(ct);
        return all.Where(e => IsDue(e, recheckDays, now)).ToList();
    }

    private static bool IsDue(MasterListEntry e, int recheckDays, DateTimeOffset now) => e.Status switch
    {
        SdsStatus.Pending => true,
        // No cap. A failed entry is always due again eventually — the only question is when. A null
        // stamp is due now, which is what makes rows written before backoff existed retryable.
        SdsStatus.Failed => e.NextAttemptUtc is null
            || DateTimeOffset.Parse(e.NextAttemptUtc, CultureInfo.InvariantCulture) <= now,
        SdsStatus.Fetched => e.LastAttemptUtc is not null
            && DateTimeOffset.Parse(e.LastAttemptUtc, CultureInfo.InvariantCulture).AddDays(recheckDays) <= now,
        // An unknown status is a bug — including a row still carrying the deleted `awaiting_operator`
        // if the migration has not run yet. Retrying is the safe direction: the cost is one wasted
        // fetch, where the cost of the old `false` was a substance nobody ever looked for again.
        _ => true,
    };

    public Task MarkFetchedAsync(MasterListEntry e, string nowUtc, CancellationToken ct)
        // Clearing the stamp matters: a stale NextAttemptUtc left over from earlier failures would
        // otherwise shadow the revision recheck that comes due RevisionRecheckDays later.
        => _store.UpsertAsync(
            e with { Status = SdsStatus.Fetched, LastAttemptUtc = nowUtc, NextAttemptUtc = null }, ct);

    public Task RecordFailureAsync(MasterListEntry e, string nowUtc, CancellationToken ct)
    {
        var attempts = e.AttemptCount + 1;
        var last = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        return _store.UpsertAsync(e with
        {
            Status = SdsStatus.Failed,
            AttemptCount = attempts,
            LastAttemptUtc = nowUtc,
            NextAttemptUtc = BackoffSchedule.NextAttemptUtc(last, attempts).ToString("O"),
        }, ct);
    }
}
