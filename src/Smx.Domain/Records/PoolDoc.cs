namespace Smx.Domain.Records;

/// One proposed marker for one component — the need-driven pool's unit. It names an ELEMENT and a
/// FORM-CLASS, never a CAS: the exact form and its check-digit-guarded CAS are Discovery's job. `FormClass`
/// is the operator's taxonomy (metal / compound / organocomplex, or a specific compound like "oxide") chosen
/// to match the substrate's physical state. It is a HYPOTHESIS, corroborated or dropped downstream — which is
/// why the agent that writes it is allowed to draw on model knowledge + web (see PoolAgent), unlike Discovery.
public sealed record PoolSuggestion(
    string Component, string Element, string FormClass,
    string Rationale, IReadOnlyList<Citation> Citations);

/// The proposed candidate pool for a project, produced from the need alone BEFORE any XRF background filter
/// (the Background stage is currently a pass-through). Written by the pool agent, or passed through verbatim
/// when the operator/eval supplied an explicit element pool (`Source = "operator"`). Discovery consumes it.
public sealed class PoolDoc
{
    public required string Id { get; set; }          // RecordIds.Pool(projectId)
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Pool;
    public List<PoolSuggestion> Suggestions { get; set; } = [];
    /// "agent" (the pool agent generated it) | "operator" (an explicit pool was supplied at intake/eval).
    public string Source { get; set; } = "agent";
}
