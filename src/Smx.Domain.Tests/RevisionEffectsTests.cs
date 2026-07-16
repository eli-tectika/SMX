using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionEffectsTests
{
    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData(Stages.Dosing, true)]       // Plan 4 — the operator changes a ppm by telling the agent why
    [InlineData(Stages.Intake, false)]
    [InlineData(Stages.Matrix, false)]      // assembled deterministically — there is no agent to re-run
    [InlineData(Stages.Cost, false)]        // deterministic table lookup — change its inputs, not the audit
    public void IsRevisable_DiscoveryRegulatoryAndDosingOnly(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.IsRevisable(stage));

    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData(Stages.Dosing, false)]      // downstream of the gate — consumes the compliant set, cannot void it
    public void BreaksRegulatoryGate_ForStagesAtOrUpstreamOfTheGate(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.BreaksRegulatoryGate(stage));

    [Theory]
    [InlineData(Stages.Matrix)]
    [InlineData(Stages.Cost)]               // not revisable — so asking what a revision would void is a bug
    public void BreaksRegulatoryGate_ThrowsForANonRevisableStage(string stage) =>
        // `false` is the DANGEROUS answer (it leaves an approved gate standing over a changed analysis),
        // so an unrecognized/non-revisable stage must never be able to fall into it.
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.BreaksRegulatoryGate(stage));

    [Fact]
    public void ConclusionKind_IsDerivedFromTheStage_NotChosenByTheAgent()
    {
        Assert.Equal(KnowledgeKinds.Material, RevisionEffects.ConclusionKind(Stages.Discovery));
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, RevisionEffects.ConclusionKind(Stages.Regulatory));
        Assert.Equal(KnowledgeKinds.Dosing, RevisionEffects.ConclusionKind(Stages.Dosing));
    }

    [Theory]
    [InlineData(Stages.Matrix)]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Cost)]               // not revisable — a Cost change has no "why" to file
    public void ConclusionKind_ThrowsForANonRevisableStage(string stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.ConclusionKind(stage));

    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    [InlineData(Stages.Dosing)]
    [InlineData(Stages.Cost)]
    public void EveryRevisableStage_HasAConclusionKindAndAGateAnswer(string stage)
    {
        // The three rules must stay in lockstep. If a later plan makes a stage revisable but forgets to
        // give it a conclusion kind, the endpoint accepts the revision, the change feed fires, the agent
        // RE-RUNS AND MUTATES THE ANALYSIS — and only then does the conclusion write throw. Catch that
        // here, at compile-and-test time, not in production after the damage is done.
        if (!RevisionEffects.IsRevisable(stage)) return;
        Assert.NotNull(RevisionEffects.ConclusionKind(stage));   // must not throw
        RevisionEffects.BreaksRegulatoryGate(stage);             // must not throw
    }
}
