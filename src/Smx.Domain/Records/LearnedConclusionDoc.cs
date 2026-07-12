namespace Smx.Domain.Records;

/// One accumulated finding with provenance + confidence (design §6.1). Authoritative in the
/// `learned-conclusions` Cosmos container (PK /kind); also pushed into the AI Search index (Plan 3b).
public sealed class LearnedConclusionDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.LearnedConclusion;
    public required string Kind { get; set; }              // KnowledgeKinds.* — the partition key
    public required ConclusionScope Scope { get; set; }
    public required string Finding { get; set; }
    public double Confidence { get; set; }
    public required ConclusionProvenance Provenance { get; set; }
    public string? Supersedes { get; set; }                // id of a conclusion this refines
    public required string CreatedAt { get; set; }         // ISO-8601 (caller-supplied; time is not available in domain)
}

public sealed record ConclusionScope(
    string? Element, string? Form, string? Material, string? Application, string? Market, string? Substance);

public sealed record ConclusionProvenance(
    IReadOnlyList<string> SourceProjects, IReadOnlyList<string> Decisions);
