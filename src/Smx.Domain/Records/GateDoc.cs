namespace Smx.Domain.Records;

public static class GateTypes
{
    public const string Regulatory = "regulatory";
    // Vp added in Plan 5 — GateDoc is deliberately generic so the VP gate reuses this machinery.
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
}
