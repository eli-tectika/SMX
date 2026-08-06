using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionEffectsTests
{
    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData(Stages.Dosing, true)]       // Plan 4 — the operator changes a ppm by telling the agent why
    [InlineData(Stages.Decision, true)]     // Plan 5 — the operator changes the pick by telling the agent why
    [InlineData(Stages.Intake, false)]
    [InlineData(Stages.Matrix, false)]      // assembled deterministically — there is no agent to re-run
    public void IsRevisable_DiscoveryRegulatoryDosingAndDecisionOnly(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.IsRevisable(stage));

    // The two BreaksRegulatoryGate tests were here. Their subject is gone: §16.4 deleted the regulatory
    // gate, so there is no signature for a revision to void and the predicate went with it. There was no
    // assertion to rewrite — a revision can no longer reach ANY live signature, because
    // PipelineRunner.ThrowIfClosedAsync refuses every revision on a project whose VP gate is approved
    // (RevisionDispatchTests pins that refusal).

    [Fact]
    public void ConclusionKind_IsDerivedFromTheStage_NotChosenByTheAgent()
    {
        Assert.Equal(KnowledgeKinds.Material, RevisionEffects.ConclusionKind(Stages.Discovery));
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, RevisionEffects.ConclusionKind(Stages.Regulatory));
        Assert.Equal(KnowledgeKinds.Dosing, RevisionEffects.ConclusionKind(Stages.Dosing));
        Assert.Equal(KnowledgeKinds.Decision, RevisionEffects.ConclusionKind(Stages.Decision));
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
    [InlineData(Stages.Dosing)]
    [InlineData(Stages.Decision)]
    public void EveryRevisableStage_HasAConclusionKindAndAGateAnswer(string stage)
    {
        // The three rules must stay in lockstep. If a later plan makes a stage revisable but forgets to
        // give it a conclusion kind, the endpoint accepts the revision, the change feed fires, the agent
        // RE-RUNS AND MUTATES THE ANALYSIS — and only then does the conclusion write throw. Catch that
        // here, at compile-and-test time, not in production after the damage is done.
        if (!RevisionEffects.IsRevisable(stage)) return;
        Assert.NotNull(RevisionEffects.ConclusionKind(stage));   // must not throw
    }
}
