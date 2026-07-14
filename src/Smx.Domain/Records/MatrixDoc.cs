namespace Smx.Domain.Records;

/// One substance × component cell.
///
/// It carries FOUR review fields, and the split between them is the design, not bookkeeping:
///
///   Proposed*      — the AGENT's proposal. It exists so the operator CONFIRMS rather than authors.
///   Determination* — the OPERATOR's signature. This is the only one CompliantSet reads, and the only one
///                    that lets a chemical into a customer's product.
///
/// The proposal is on the cell because a proposal the operator cannot SEE is a proposal they cannot confirm —
/// the feature would be inert, and they would go on authoring every determination by hand. It is rendered
/// beside the operator's field and must never be rendered AS it: a UI that collapses the two into one column
/// is the agent signing the regulatory gate, which is the single thing Law 9 exists to prevent.
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
