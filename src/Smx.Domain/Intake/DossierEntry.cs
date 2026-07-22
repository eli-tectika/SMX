namespace Smx.Domain.Intake;

/// What is known about one catalogue question.
///
/// NOTE WHAT IS ABSENT: there is no state for "never asked". A question with no entry at all is a
/// question the agent has not reached, and IntakeGate refuses while any exist. This is the whole
/// point of the dossier layer — the headline harm in this system is a FALSE PASS, and prose cannot
/// distinguish "the client says there are no by-products" from "we never got to that question".
/// Both read as silence. A state named `NotAsked` would put that silence back.
public static class DossierState
{
    /// The operator told us, or it was read out of an attachment.
    public const string Answered = "answered";
    /// The agent inferred it. REQUIRES a confidence — see IntakeGate.
    public const string AgentProposed = "agent-proposed";
    /// Asked, and the answer is genuinely not known. Travels downstream as a stated gap.
    public const string Unknown = "unknown";
    /// Asked, and the question does not apply to this project.
    public const string NotApplicable = "not-applicable";

    public static readonly string[] All = [Answered, AgentProposed, Unknown, NotApplicable];
}

public sealed record DossierEntry
{
    public required string QuestionId { get; init; }
    public required string State { get; init; }
    public string Answer { get; init; } = "";
    /// `operator`, `file:{fileId}`, or `agent`. Free-form on purpose — an operator describing an
    /// unreadable attachment is `operator`, and saying which file is part of the answer, not the tag.
    public string Provenance { get; init; } = "";
    /// Required when State is AgentProposed, forbidden to be meaningless otherwise.
    public string? Confidence { get; init; }
    public string RecordedAt { get; init; } = "";
}
