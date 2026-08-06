using Smx.Domain.Records;

namespace Smx.Domain;

/// The rules governing revise-with-reason (design §4/§6.1). Pure, so the safety-critical ones can be
/// asserted without standing up a dispatcher, a store, or an agent.
public static class RevisionEffects
{
    /// Revising a stage means RE-RUNNING its agent. Discovery, Regulatory and Dosing qualify; Matrix does
    /// not (it is assembled deterministically from candidates + verdicts — revise those instead), and
    /// neither did Cost, the deterministic lookup that has since been deleted (to change it you changed its inputs, not
    /// argue with the audit; Law 4 has no "why" to record over a price fetch).
    ///
    /// Dosing IS revisable (Law 4): the operator changes a ppm by telling the agent WHY, which re-runs the
    /// stage and earns a Learned Conclusion — the same mechanism as a tiering or a verdict change.
    ///
    /// Decision IS revisable (Plan 5, Task 15): the pick is the agent's PROPOSAL, and the operator changes
    /// it the same way — never by hand, always with a reason the knowledge layer keeps. The executor
    /// (PipelineRunner.ReviseDecisionAsync) refuses outright once the project is CLOSED (VP gate
    /// approved): the signature is history, and revising history is a new project decision.
    ///
    /// Intake is deliberately excluded even though it DOES have an agent: its output is the derived
    /// regulatory scope that every downstream stage was screened against, so re-running it invalidates
    /// the whole project rather than one stage's output. That is a bigger blast radius than
    /// revise-with-reason is meant to have; no journey step asks for it.
    public static bool IsRevisable(string stage) =>
        stage is Stages.Discovery or Stages.Regulatory or Stages.Dosing or Stages.Decision;

    // `BreaksRegulatoryGate` lived here. It answered which revisions void the R.E.'s signature, and the
    // 2026-08-06 redesign §16.4 deleted that signature — there is no regulatory GateDoc to void. The one
    // remaining signature (VP) needs no equivalent: an approved VP gate IS the project's close, and
    // PipelineRunner.ThrowIfClosedAsync refuses EVERY revision on a closed project before an agent runs.
    // So no revision can reach a live signature, and a predicate saying which ones would could only be a
    // check that cannot fire.
    //
    // What the deleted predicate really protected — an operator's review silently absorbing verdicts they
    // never saw — is intact: a revised VerdictDoc comes back with `EvidenceReviewed=false`, and
    // EvidenceReview.Outstanding refuses the order and the VP pen until the operator opens it again.

    /// Which kind of Learned Conclusion a revision to this stage yields — also the Cosmos partition key.
    /// Code decides this, never the agent: a tiering change is a material finding; a verdict change is a
    /// regulatory judgment. Letting a model pick its own partition key would let it file a regulatory
    /// judgment where no regulatory reader will ever look for it.
    public static string ConclusionKind(string stage) => stage switch
    {
        Stages.Discovery => KnowledgeKinds.Material,
        Stages.Regulatory => KnowledgeKinds.RegulatoryJudgment,
        Stages.Dosing => KnowledgeKinds.Dosing,
        Stages.Decision => KnowledgeKinds.Decision,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
            "no conclusion kind for this stage — it is not revisable"),
    };
}
