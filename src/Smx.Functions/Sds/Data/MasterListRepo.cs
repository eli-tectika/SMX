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

    public async Task<IReadOnlyList<MasterListEntry>> QueryDueAsync(
        int retryCap, int recheckDays, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        var all = await _store.ListAllAsync(ct);
        return all.Where(e => IsDue(e, retryCap, recheckDays, now)).ToList();
    }

    private static bool IsDue(MasterListEntry e, int retryCap, int recheckDays, DateTimeOffset now) => e.Status switch
    {
        SdsStatus.Pending => true,
        SdsStatus.Failed => e.AttemptCount < retryCap,
        SdsStatus.Fetched => e.LastAttemptUtc is not null
            && DateTimeOffset.Parse(e.LastAttemptUtc, CultureInfo.InvariantCulture).AddDays(recheckDays) <= now,
        _ => false, // awaiting_operator is NOT auto-retried
    };

    public Task MarkFetchedAsync(MasterListEntry e, string nowUtc, CancellationToken ct)
        => _store.UpsertAsync(e with { Status = SdsStatus.Fetched, LastAttemptUtc = nowUtc }, ct);

    public Task RecordFailureAsync(MasterListEntry e, int retryCap, string nowUtc, CancellationToken ct)
    {
        var attempts = e.AttemptCount + 1;
        var status = attempts >= retryCap ? SdsStatus.AwaitingOperator : SdsStatus.Failed;
        return _store.UpsertAsync(e with { Status = status, AttemptCount = attempts, LastAttemptUtc = nowUtc }, ct);
    }
}
