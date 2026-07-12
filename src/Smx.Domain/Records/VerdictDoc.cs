namespace Smx.Domain.Records;

public enum VerdictStatus { Pass, Conditional, NeedsReview, Fail }
public enum VerdictDimension { Compatibility, ElementGate, ApplicationCheck, Hazard }

public sealed record DimensionVerdict(
    string Dimension, VerdictStatus Status, IReadOnlyList<Citation> Citations, double Confidence, string Rationale);

public sealed class VerdictDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Verdict;
    public required string Cas { get; set; }
    public required string ComponentId { get; set; }
    public required string Element { get; set; }
    public required string Form { get; set; }
    public List<DimensionVerdict> Dimensions { get; set; } = [];
    // Operator inputs (Regulatory gate, Plan 2) — distinct from the agent's Dimensions above.
    public bool EvidenceReviewed { get; set; }
    public string? Determination { get; set; }        // null | "recommended" | "rejected"
    public string? DeterminationReason { get; set; }  // required when Determination == "rejected"
    public VerdictStatus Overall => Fold(Dimensions);

    public static VerdictStatus Fold(IReadOnlyList<DimensionVerdict> dims) =>
        dims.Count == 0 ? VerdictStatus.NeedsReview : dims.Max(d => d.Status);
}
