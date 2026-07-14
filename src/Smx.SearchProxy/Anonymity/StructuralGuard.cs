using System.Text.RegularExpressions;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Anonymity;

public sealed record GuardVerdict(bool Allowed, string? Reason)
{
    public static readonly GuardVerdict Ok = new(true, null);
    public static GuardVerdict Block(string reason) => new(false, reason);
}

/// Layer 2 of the anonymization (spec §6.2). PROJECT-BLIND: it holds no client names, no project ids, no
/// customer roster — putting that list in git on the internet-facing component would be the wrong trade in
/// the wrong place. It rejects strings SHAPED like identifiers. The layer that knows the actual names is
/// SensitiveTermGuard, in the orchestrator, where the names already live.
public sealed partial class StructuralGuard(ProxyOptions opts)
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidLike();

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]{2,}")]
    private static partial Regex EmailLike();

    [GeneratedRegex(@"(https?://|www\.)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    /// Seven or more CONSECUTIVE digits. A CAS number (1314-36-9) is hyphen-separated and survives; an order
    /// number, a phone number or a batch id does not.
    [GeneratedRegex(@"\d{7,}")]
    private static partial Regex DigitRun();

    public GuardVerdict Check(SearchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Query)) return GuardVerdict.Block("query_empty");
        if (req.Query.Length > opts.MaxQueryChars) return GuardVerdict.Block("query_too_long");
        if (!SearchIntents.All.Contains(req.Intent)) return GuardVerdict.Block("unknown_intent");
        // The ceiling is the OPERATOR'S (PROXY_MAX_RESULTS), not a constant baked in here — otherwise the
        // setting is a lie that binds nothing. The lower bound stays fixed: 0 results is a malformed ask.
        if (req.MaxResults < 1 || req.MaxResults > opts.MaxResults) return GuardVerdict.Block("max_results_out_of_range");

        if (GuidLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_guid");
        if (EmailLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_email");
        if (UrlLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_url");
        if (DigitRun().IsMatch(req.Query)) return GuardVerdict.Block("contains_digit_run");

        return GuardVerdict.Ok;
    }
}
