namespace Smx.Domain.Records;

public static class GateTypes
{
    public const string Regulatory = "regulatory";
    public const string Vp = "vp";
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
