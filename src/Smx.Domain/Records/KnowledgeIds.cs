namespace Smx.Domain.Records;

/// Record-type discriminators for the cross-project knowledge containers (parallel to RecordTypes,
/// which is for the per-project `record` change-feed bus). These docs live OUTSIDE that bus.
public static class KnowledgeTypes
{
    public const string LearnedConclusion = "learned-conclusion";
    public const string MarkerLibrary = "marker-library";
    public const string MsdsRegistry = "msds-registry";
}

/// The kind of a Learned Conclusion — also its Cosmos partition key (/kind). Distinct from the
/// record-type discriminator above (which is always "learned-conclusion").
public static class KnowledgeKinds
{
    public const string Material = "material";
    public const string XrfBackground = "xrf-background";
    public const string RegulatoryJudgment = "regulatory-judgment";
}

public static class KnowledgeIds
{
    public static string LearnedConclusion(string kind, string scopeKey) => $"{kind}|{scopeKey}";
    public static string Marker(string key) => $"marker|{key}";
    public static string Msds(string cas) => $"msds|{cas}";
}
