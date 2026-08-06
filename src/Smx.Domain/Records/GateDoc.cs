namespace Smx.Domain.Records;

/// THERE IS EXACTLY ONE GATE. `Regulatory` was deleted by the 2026-08-06 redesign §16.4 — no GateDoc, no
/// approve endpoint, no signature; the matrix carries confidence and sources and IS the review surface.
/// Do not reintroduce a second constant here without reading §16.4: the regulatory gate was the writer of
/// every operator `Determination`, and deleting it is what forced <see cref="Smx.Domain.CompliantSet"/>'s
/// rule to change. A third gate would dilute what a signature means (see DosingDoc.ReviewNote).
public static class GateTypes
{
    public const string Vp = "vp";
}

/// The values `GateDoc.ApprovedBy` may take. Constants rather than literals because these strings are read
/// by the UI as a closed set: a typo would not fail anywhere, it would render as UNKNOWN PROVENANCE — a
/// gate whose signature the system quietly disowns.
public static class GateSigners
{
    /// A human recording a determination through the gate's own endpoint. The ONLY value anything still
    /// writes.
    public const string Operator = "operator";

    /// REGULATORY_AUTO_APPROVE signing on nobody's behalf. ITS WRITER IS GONE — the flag and the regulatory
    /// gate were deleted together (§16.4), and there was never a VP equivalent, nor must there be one.
    ///
    /// The constant SURVIVES on purpose. Gate documents carrying it are on file in deployed environments,
    /// where the flag defaulted ON, and the UI renders this value as an alarm in the largest type on the
    /// screen. Delete the constant and those historical machine signatures stop being recognised — they
    /// would render as an unremarkable approval, which is the quiet reading of the loudest thing the record
    /// can say.
    public const string AutoApprove = "auto-approve";
}

/// Operator-signed set-level gate record. Per-cell determinations live on the VerdictDoc.
public sealed class GateDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Gate;
    public required string GateType { get; set; }        // GateTypes.*
    public string Status { get; set; } = "locked";       // "locked" | "approved"
    public string? Reason { get; set; }
    public string? ApprovedAt { get; set; }

    /// WHAT approved this gate — "operator" | "auto-approve" | null.
    ///
    /// Null is not "human": it is a gate written before this field existed, or one never approved.
    /// The distinction is the point. REGULATORY_AUTO_APPROVE used to sign gates itself, and without a
    /// recorded signer a machine signature is indistinguishable from a human determination on every
    /// surface that reads this record. That writer is gone, but the records it wrote are not.
    /// Consumers must treat null-on-approved as UNKNOWN, never as human.
    public string? ApprovedBy { get; set; }
}
