namespace Smx.Domain.Records;

public enum VerdictStatus { Pass, Conditional, NeedsReview, Fail }
public enum VerdictDimension { Compatibility, ElementGate, ApplicationCheck, Hazard }

public sealed record DimensionVerdict(
    string Dimension, VerdictStatus Status, IReadOnlyList<Citation> Citations, double Confidence, string Rationale);

/// The R.E.'s ruling on a substance × component. One constant, because the endpoint that records it, the
/// compliant-set filter that reads it, and the fakes must not drift apart on the string that decides
/// whether a chemical goes into a customer's product.
public static class Determinations
{
    public const string Recommended = "recommended";
    public const string Rejected = "rejected";
}

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
    // Determination is the signature CompliantSet reads: only `recommended` here lets this substance be
    // dosed. The determination endpoint is its only writer, and it 422s anything that is not exactly one of
    // the two constants, reason included (both rulings carry one — an override of a Fail most of all).
    public bool EvidenceReviewed { get; set; }
    public string? Determination { get; set; }        // null | Determinations.Recommended | Determinations.Rejected
    public string? DeterminationReason { get; set; }  // required for EITHER determination
    // The AGENT's proposal (Plan 4). It exists so the operator CONFIRMS rather than authors — nothing more.
    // It is deliberately a SEPARATE field from Determination: if a proposal could be read as a
    // determination, the agent would be signing the regulatory gate through the back door. CompliantSet
    // ignores these two fields entirely, and a test pins that.
    public string? ProposedDetermination { get; set; }   // null | Determinations.*
    public string? ProposedReason { get; set; }
    public VerdictStatus Overall => Fold(Dimensions);

    public static VerdictStatus Fold(IReadOnlyList<DimensionVerdict> dims) =>
        dims.Count == 0 ? VerdictStatus.NeedsReview : dims.Max(d => d.Status);
}
