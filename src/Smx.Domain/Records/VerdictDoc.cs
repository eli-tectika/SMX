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
    // Operator inputs — distinct from the agent's Dimensions above, and now an OVERRIDE rather than the
    // sole admission. The regulatory gate that used to be their only writer is gone (2026-08-06 §16.4), so
    // CompliantSet admits the agent's proposal by default and reads this field to VETO: `rejected` here
    // takes a substance out no matter what the agent proposed, and it is unrescuable. POST
    // /regulatory/determination is still the only writer, and it still 422s anything that is not exactly
    // one of the two constants, reason included (both rulings carry one — an override of a Fail most of all).
    //
    // EvidenceReviewed is the OTHER operator input, and it is read by EvidenceReview.Outstanding: a live
    // non-Pass verdict nobody has opened blocks the VP signature and the order. Nothing machine-side sets
    // it any more — REGULATORY_AUTO_APPROVE was deleted with the gate.
    public bool EvidenceReviewed { get; set; }
    public string? Determination { get; set; }        // null | Determinations.Recommended | Determinations.Rejected
    public string? DeterminationReason { get; set; }  // required for EITHER determination
    // The AGENT's proposal. Still a SEPARATE field from Determination, and the separation carries MORE
    // weight now that it is the default admission: every surface that shows a determination must be able
    // to say whether a human or the machine put it there. Collapsing them would make an unruled project
    // indistinguishable from a ruled one on every screen. MatrixCell and RegulatoryCells pin the split.
    public string? ProposedDetermination { get; set; }   // null | Determinations.*
    public string? ProposedReason { get; set; }
    public VerdictStatus Overall => Fold(Dimensions);

    public static VerdictStatus Fold(IReadOnlyList<DimensionVerdict> dims) =>
        dims.Count == 0 ? VerdictStatus.NeedsReview : dims.Max(d => d.Status);
}
