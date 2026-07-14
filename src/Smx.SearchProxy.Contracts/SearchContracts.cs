namespace Smx.SearchProxy.Contracts;

/// The request the orchestrator sends to the Search Proxy.
///
/// PROJECT-BLIND BY CONSTRUCTION. There is deliberately no projectId, client, product, correlation-id or
/// url field here, and the trigger deserializes with UnmappedMemberHandling.Disallow — so a caller cannot
/// smuggle one in. This is stronger than scrubbing a project identifier out: there is nothing to scrub.
public sealed record SearchRequest(
    string Query,
    string Intent,
    int MaxResults = 10,
    int? FreshnessDays = null);

/// One normalized result. `Url` is where the operator can go to check the claim; the proxy itself never
/// fetches it (spec §3, invariant 2 — no fetch interface).
public sealed record SearchHit(
    string Title,
    string Url,
    string Snippet,
    string Host,
    string? Age);

/// `CoverCount` is how many queries actually egressed (real + decoys); 0 on a cache hit.
public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Results,
    int ResultCount,
    bool CacheHit,
    int CoverCount);

/// `Reason` is a machine-readable token (e.g. "contains_guid"); the caller relays it to the model as an
/// instructive note so it can rephrase, rather than silently getting nothing back.
public sealed record SearchError(string Reason, string Message);

/// The intent selects which decoy family the cover batch is drawn from. Adding an intent means adding a
/// decoy family to cover-corpus.json — the corpus loader enforces that (CoverCorpus.FromJson throws if an
/// intent has no family), so a new intent cannot ship without its cover.
public static class SearchIntents
{
    public const string CandidateForms = "discovery.candidate_forms";
    public const string FormProperties = "discovery.form_properties";
    public const string SupplierAvailability = "discovery.supplier_availability";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CandidateForms, FormProperties, SupplierAvailability };
}
