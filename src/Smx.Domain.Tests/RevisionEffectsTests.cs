using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionEffectsTests
{
    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData(Stages.Intake, false)]
    [InlineData(Stages.Matrix, false)]      // assembled deterministically — there is no agent to re-run
    [InlineData("dosing", false)]           // Plan 4
    public void IsRevisable_DiscoveryAndRegulatoryOnly(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.IsRevisable(stage));

    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    public void BreaksRegulatoryGate_ForStagesAtOrUpstreamOfTheGate(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.BreaksRegulatoryGate(stage));

    [Fact]
    public void BreaksRegulatoryGate_ThrowsForANonRevisableStage() =>
        // `false` is the DANGEROUS answer (it leaves an approved gate standing over a changed analysis),
        // so an unrecognized stage must never be able to fall into it.
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.BreaksRegulatoryGate(Stages.Matrix));

    [Fact]
    public void ConclusionKind_IsDerivedFromTheStage_NotChosenByTheAgent()
    {
        Assert.Equal(KnowledgeKinds.Material, RevisionEffects.ConclusionKind(Stages.Discovery));
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, RevisionEffects.ConclusionKind(Stages.Regulatory));
    }

    [Theory]
    [InlineData(Stages.Matrix)]
    [InlineData(Stages.Intake)]
    public void ConclusionKind_ThrowsForANonRevisableStage(string stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.ConclusionKind(stage));

    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    [InlineData("dosing")]
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
