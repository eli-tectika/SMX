namespace Smx.Domain.Records;

public sealed record Citation(string Source, string Reference, string RetrievedAt, string? Snippet = null);

public sealed record ComponentSpec(string Id, string Material, string Application, IReadOnlyList<string> Markets, string Objective);
public sealed record SubstanceSpec(string Element, string Form, string Cas);
public sealed record AppliedList(string ListId, string ComponentId, string Reason, Citation Citation);

public sealed record ElementPool(string Component, string Element, string Line, string Status, string? SignalNote = null); // Status: "V" | "L"

public sealed record CandidateSubstance(
    string ComponentId, string Element, string Form, string Cas,
    string? ParticleSize, string? Solvent, bool Preferred, string Tier, string Rationale,
    IReadOnlyList<Citation> Citations); // Tier: "A" | "B" | "C"

public sealed class ConstraintsDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Constraints;
    public List<ComponentSpec> Components { get; set; } = [];
    public List<SubstanceSpec> Substances { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    /// Derived regulatory scope: which lists apply, per component (element gate entries use ComponentId="*").
    public List<AppliedList> DerivedScope { get; set; } = [];
}
