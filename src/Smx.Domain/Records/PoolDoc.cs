namespace Smx.Domain.Records;

/// One ELEMENT chosen for one component — the answer to the pool pass's FIRST question: which elements are
/// detectable on this substrate, clean against the background it is expected to carry, and plausible at all
/// for this application and these markets. No molecular form has been considered yet.
///
/// It is PERSISTED, not merely reasoned over and thrown away, because the element question and the form
/// question rest on different evidence (what is readable here vs. what survives this material). A PoolDoc
/// carrying only <see cref="PoolSuggestion"/> keeps the answer to the second question and silently loses the
/// first — which is exactly the collapse the two-step pass exists to undo, and it would be invisible in the
/// record. Defaulted empty on <see cref="PoolDoc"/> so pools written before the split still deserialize.
public sealed record PoolElementChoice(
    string Component, string Element, string Rationale, IReadOnlyList<Citation> Citations);

/// One proposed marker for one component — the need-driven pool's unit, and the answer to the pool pass's
/// SECOND question: which molecular form of an already-chosen element suits this substrate. It names an
/// ELEMENT and a FORM-CLASS, never a CAS: the exact form and its check-digit-guarded CAS belong to Discovery's
/// corroboration pass. `FormClass` is the operator's taxonomy (metal / compound / organocomplex, or a specific
/// compound like "oxide") chosen to match the substrate's physical state. It is a HYPOTHESIS, corroborated or
/// dropped downstream — which is why the pass that writes it may draw on model knowledge + web (see
/// DiscoveryAgent.PoolInstructions), unlike the corroboration pass.
public sealed record PoolSuggestion(
    string Component, string Element, string FormClass,
    string Rationale, IReadOnlyList<Citation> Citations);

/// The proposed candidate pool for a project, produced from the need alone BEFORE any XRF background filter
/// (the Background stage is currently a pass-through). Written by the DISCOVERY agent's pool pass, or passed
/// through verbatim when the operator/eval supplied an explicit element pool (`Source = "operator"`). The
/// corroboration pass consumes it.
public sealed class PoolDoc
{
    public required string Id { get; set; }          // RecordIds.Pool(projectId)
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Pool;
    /// Step 1 — the elements chosen per component, with why. Empty on pools written before the
    /// elements-then-molecules split, and on operator-supplied pools (which arrive as elements already).
    public List<PoolElementChoice> Elements { get; set; } = [];
    /// Step 2 — each chosen element broken into the forms that suit the substrate.
    public List<PoolSuggestion> Suggestions { get; set; } = [];
    /// "agent" (the Discovery agent's pool pass generated it) | "operator" (an explicit pool was supplied at
    /// intake/eval).
    public string Source { get; set; } = "agent";
}
