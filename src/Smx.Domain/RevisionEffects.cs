using Smx.Domain.Records;

namespace Smx.Domain;

/// The rules governing revise-with-reason (design §4/§6.1). Pure, so the safety-critical ones can be
/// asserted without standing up a dispatcher, a store, or an agent.
public static class RevisionEffects
{
    /// Revising a stage means RE-RUNNING its agent. Discovery, Regulatory and Dosing qualify; Matrix does
    /// not (it is assembled deterministically from candidates + verdicts — revise those instead), and
    /// neither does Cost (it is a deterministic table lookup — to change a cost you change its inputs, not
    /// argue with the audit; Law 4 has no "why" to record over a price fetch).
    ///
    /// Dosing IS revisable (Law 4): the operator changes a ppm by telling the agent WHY, which re-runs the
    /// stage and earns a Learned Conclusion — the same mechanism as a tiering or a verdict change.
    ///
    /// Intake is deliberately excluded even though it DOES have an agent: its output is the derived
    /// regulatory scope that every downstream stage was screened against, so re-running it invalidates
    /// the whole project rather than one stage's output. That is a bigger blast radius than
    /// revise-with-reason is meant to have; no journey step asks for it.
    public static bool IsRevisable(string stage) =>
        stage is Stages.Discovery or Stages.Regulatory or Stages.Dosing;

    /// A gate is an operator's signature over a SPECIFIC analysis. Re-running an agent at or upstream of
    /// the Regulatory gate replaces that analysis, so the signature is void and has to be re-taken.
    ///
    /// This is not bookkeeping — it is the false-pass guard. StageDispatcher.TryAssembleAsync will not
    /// lower a stage that already reached `done`, so an approved gate left standing would let a `done`
    /// Regulatory stage silently absorb the brand-new, UNREVIEWED verdicts a revision produces: the
    /// operator's signature would then cover verdicts they never saw.
    ///
    /// It THROWS rather than defaulting for an unknown stage, on purpose. `false` is the dangerous
    /// answer here, so it must never be the one an unrecognized string falls into. Call IsRevisable
    /// first — which every caller must do anyway. Dosing is DOWNSTREAM of the gate: it consumes the
    /// compliant set the operator signed over, it cannot change it, so re-running it must NOT void that
    /// signature — it answers `false` here. (Cost is not revisable, so it hits the throw above.)
    public static bool BreaksRegulatoryGate(string stage)
    {
        if (!IsRevisable(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "not a revisable stage — check IsRevisable before asking what a revision would void");
        return stage is Stages.Discovery or Stages.Regulatory;
    }

    /// Which kind of Learned Conclusion a revision to this stage yields — also the Cosmos partition key.
    /// Code decides this, never the agent: a tiering change is a material finding; a verdict change is a
    /// regulatory judgment. Letting a model pick its own partition key would let it file a regulatory
    /// judgment where no regulatory reader will ever look for it.
    public static string ConclusionKind(string stage) => stage switch
    {
        Stages.Discovery => KnowledgeKinds.Material,
        Stages.Regulatory => KnowledgeKinds.RegulatoryJudgment,
        Stages.Dosing => KnowledgeKinds.Dosing,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
            "no conclusion kind for this stage — it is not revisable"),
    };
}
