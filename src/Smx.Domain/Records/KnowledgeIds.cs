namespace Smx.Domain.Records;

/// Record-type discriminators for the cross-project knowledge containers (parallel to RecordTypes,
/// which is for the per-project `record` change-feed bus). These docs live OUTSIDE that bus.
public static class KnowledgeTypes
{
    public const string LearnedConclusion = "learned-conclusion";
    public const string MarkerLibrary = "marker-library";
    public const string MsdsRegistry = "msds-registry";
    public const string SubstanceProperty = "substance-property";
}

/// The kind of a Learned Conclusion — also its Cosmos partition key (/kind). Distinct from the
/// record-type discriminator above (which is always "learned-conclusion").
public static class KnowledgeKinds
{
    public const string Material = "material";
    public const string XrfBackground = "xrf-background";
    public const string RegulatoryJudgment = "regulatory-judgment";
    public const string Dosing = "dosing";
}

/// Lifecycle of a Marker Library code. Only `Approved` codes are offered as reuse candidates;
/// Plan 5 introduces the others. One constant so the doc default, the Cosmos filter and the fake
/// cannot drift apart on the string that decides whether a retired code can be reused.
public static class MarkerStatus
{
    public const string Approved = "approved";
}

/// Review state of an MSDS Registry entry. This gates procurement (the MSDS-before-order hard gate,
/// Plan 5), so — as with MarkerStatus — the doc default, the endpoint that signs the review, and any
/// future reader must share one literal rather than three.
public static class MsdsReviewStatus
{
    public const string Unreviewed = "unreviewed";
    public const string Reviewed = "reviewed";
}

public static class KnowledgeIds
{
    public static string LearnedConclusion(string kind, string scopeKey) => $"{kind}|{scopeKey}";
    public static string Marker(string key) => $"marker|{key}";
    public static string Msds(string cas) => $"msds|{cas}";

    /// Keyed by CAS alone — deliberately NOT by project. The whole point is that project B's Dosing finds
    /// the loading project A entered.
    public static string SubstanceProperty(string cas) => $"substance-property|{cas}";

    /// A revise-with-reason conclusion is keyed by the DECISION that produced it, not by its scope.
    /// Both halves matter: re-delivering the same revision upserts the same doc (idempotent under an
    /// at-least-once change feed), while a LATER revision on the same scope writes a NEW conclusion
    /// rather than overwriting the old one — design §6.1 is "accumulation, not overwrite".
    public static string RevisionConclusion(string kind, string revisionId) => LearnedConclusion(kind, revisionId);
}
