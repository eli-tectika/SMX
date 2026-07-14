using System.Globalization;
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Pipeline;

/// Two bounds, both on PROVIDER CALLS (decoys included — that is what the bill and the egress log count):
///   • a monthly cap, so a runaway agent loop is a 429 rather than an invoice;
///   • a per-minute bucket, so a burst cannot spray egress even inside the cap.
/// Both are deliberately crude. They are a backstop against our own bugs, not a billing system.
public sealed class QuotaGuard(IQuotaStore store, ProxyOptions opts)
{
    // System.Threading.Lock is .NET 9+; this app targets net8.0, so the gate is a plain object.
    private readonly object _gate = new();
    private string _minute = "";
    private int _minuteCount;

    public async Task<bool> TryConsumeAsync(int providerCalls, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        lock (_gate)
        {
            var minute = now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            if (minute != _minute) { _minute = minute; _minuteCount = 0; }
            if (_minuteCount + providerCalls > opts.RateLimitPerMinute) return false;
            _minuteCount += providerCalls;
        }

        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var spent = await store.ReadAsync(month, ct);
        if (spent + providerCalls > opts.MonthlyQueryCap) return false;

        await store.AddAsync(month, providerCalls, ct);
        return true;
    }
}
