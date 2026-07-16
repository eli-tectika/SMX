namespace Smx.Domain.Records;

/// Which criteria a row has actually cleared — booleans computed by DecisionAssembler from the RECORD
/// (a recommended determination, a dosable window, a priced audit), never asserted by the agent.
public sealed record ClearedCriteria(bool Regulatory, bool Dosing, bool Cost);

/// Where each claim in a row came from — record ids, so every figure on the decision matrix is
/// traceable end-to-end (§3.5: "every row traceable"). Ids, not copies: the record is the truth.
public sealed record TraceRefs(string Verdict, string Window, string Audit);

/// One substance's line in a component's decision: the operator's determination (copied from the
/// verdict — the R.E.'s word, not the agent's), the recommended ppm from Dosing, and what it cleared.
public sealed record DecisionRow(
    string Cas, string Element, string Determination, double RecommendedPpm,
    ClearedCriteria Cleared, TraceRefs Traceability);

/// The agent's RECOMMENDED final code for a component: identified by its ratio signature plus the
/// marker CAS list, with the rationale the VP reads. A PROPOSAL — see ComponentDecision.
public sealed record ProposedCode(string RatioSignature, IReadOnlyList<string> MarkerCas, string Rationale);

/// A component's decision. ProposedCode is the AGENT's; ConfirmedCode/ConfirmedBy/ConfirmedReason are
/// the VP's, written ONLY by POST …/decision/determination. The split is Law 9 in a type: nothing that
/// reads ConfirmedCode can mistake a proposal for a signature, because a proposal never occupies it.
public sealed record ComponentDecision(
    string ComponentId, IReadOnlyList<DecisionRow> Rows, ProposedCode? ProposedCode,
    string? ConfirmedCode = null, string? ConfirmedBy = null, string? ConfirmedReason = null);

public static class ProcurementStatus
{
    public const string Unreleased = "unreleased"; // before the VP gate
    public const string Released = "released";     // VP signed; individual orders still gated by MSDS
}

/// Procurement is a STATE FLAG on the decision (§4: no real ordering system in scope) plus the list of
/// substances actually ordered — each order individually gated by the MSDS-before-order precondition.
public sealed class ProcurementState
{
    public string Status { get; set; } = ProcurementStatus.Unreleased;
    public List<string> OrderedCas { get; set; } = [];
}

public sealed class DecisionDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Decision;
    public List<ComponentDecision> Components { get; set; } = [];
    public ProcurementState Procurement { get; set; } = new();
    public required string GeneratedAt { get; set; }
}
