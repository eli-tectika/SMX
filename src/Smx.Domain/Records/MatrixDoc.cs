namespace Smx.Domain.Records;

/// One substance × component cell.
///
/// It carries FOUR review fields, and the split between them is the design, not bookkeeping:
///
///   Proposed*      — the AGENT's proposal, and since §16.4 the DEFAULT admission to CompliantSet.
///   Determination* — the OPERATOR's override. `rejected` here takes a substance out no matter what the
///                    agent proposed; `recommended` overrules an agent's refusal.
///
/// The proposal is on the cell because this matrix IS the review surface now — the regulatory gate that
/// used to be it was deleted, and an operator who cannot see what the machine proposed has nothing to
/// override. It is rendered beside the operator's field and must never be rendered AS it: a UI that
/// collapses the two into one column makes "nobody has ruled on this" look exactly like "a human ruled on
/// this", on the screen that exists to tell them apart.
public sealed record MatrixCell(
    string Cas, string ComponentId, VerdictStatus Overall, List<DimensionVerdict> Dimensions,
    string? ProposedDetermination = null, string? ProposedReason = null,
    string? Determination = null, string? DeterminationReason = null, bool EvidenceReviewed = false);

public sealed class MatrixDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Matrix;
    public List<SubstanceSpec> Rows { get; set; } = [];      // substances
    public List<string> Columns { get; set; } = [];          // component ids
    public List<MatrixCell> Cells { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
}
