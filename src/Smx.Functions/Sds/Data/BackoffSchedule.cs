namespace Smx.Functions.Sds.Data;

/// The retry cadence for a substance whose sheet could not be fetched. Pure arithmetic, deliberately
/// separate from MasterListRepo so it is testable without a store.
///
/// This replaces SDS_RETRY_CAP, which was not a cadence but an ending: three failures moved an entry to
/// `awaiting_operator`, a status IsDue never returned, and there was no reset operation anywhere in the
/// codebase. On 2026-07-29 that had silently consumed 40 of 53 substances in dev — the weekly timer was
/// still firing and finding nothing to do. Backoff means a dead supplier stops being hammered WITHOUT the
/// system ever giving up on it.
public static class BackoffSchedule
{
    public const int MaxDelayDays = 32;

    /// 1, 2, 4, 8, 16, then pinned at 32 days.
    public static int DelayDays(int attemptCount)
    {
        // The cap is applied BEFORE the shift, not after: `1 << (attemptCount - 1)` is undefined for a
        // shift of 32 or more and silently wrong well before that, so a pathological attempt count must
        // never reach the arithmetic.
        if (attemptCount <= 1) return 1;
        if (attemptCount >= 6) return MaxDelayDays;
        return 1 << (attemptCount - 1);
    }

    public static DateTimeOffset NextAttemptUtc(DateTimeOffset lastAttemptUtc, int attemptCount)
        => lastAttemptUtc.AddDays(DelayDays(attemptCount));
}
