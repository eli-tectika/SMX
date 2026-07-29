namespace Smx.Domain.Records;

public static class GateTypes
{
    public const string Regulatory = "regulatory";
    public const string Vp = "vp";
}

/// The values `GateDoc.ApprovedBy` may take. Constants rather than literals because these strings are
/// written in three places (both hard-gate endpoints and the auto-approve path) and read by the UI as a
/// closed set: a typo would not fail anywhere, it would render as UNKNOWN PROVENANCE — a gate whose
/// signature the system quietly disowns.
public static class GateSigners
{
    /// A human recording a determination through the gate's own endpoint.
    public const string Operator = "operator";

    /// REGULATORY_AUTO_APPROVE signing on nobody's behalf. There is no VP equivalent and must not be one.
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
    /// The distinction is the point. REGULATORY_AUTO_APPROVE signs gates itself, and without a
    /// recorded signer a machine signature is indistinguishable from the R.E.'s determination on
    /// every surface that reads this record — which is precisely what the hard gate exists to
    /// prevent. Consumers must treat null-on-approved as UNKNOWN, never as human.
    public string? ApprovedBy { get; set; }
}
