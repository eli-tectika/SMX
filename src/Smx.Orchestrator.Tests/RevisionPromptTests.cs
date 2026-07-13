using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The operator never hand-edits an agent's output (Law 4): they say WHY, and the agent re-runs applying
/// the change. These tests pin the one thing that makes that work — the operator's target AND their
/// verbatim reason actually reaching the model. The reason is not decoration, it IS the instruction: an
/// agent re-run without it would faithfully reproduce the very output the operator rejected.
///
/// (Reuses ScriptedAgent, which already records every prompt it is sent in `Received`.)
public class RevisionPromptTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Y", "Kα", "V", null)],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private static CandidateSubstance Candidate() =>
        new("bottle", "Y", "2-ethylhexanoate", "136-25-4", null, null, true, "A", "ok",
            [new Citation("catalog", "ref-catalog/x", "t")]);

    private const string Target = "move Y to tier C";
    private const string Reason = "Y overlaps the Zr line in this HDPE background";

    private static RevisionDoc Revision(string stage) => new()
    {
        Id = RecordIds.Revision("p1", stage, "r1"), ProjectId = "p1", Stage = stage,
        Target = Target, Reason = Reason,
        CreatedAt = "2026-07-13T00:00:00.0000000+00:00",
    };

    private const string ValidDiscovery = """
    { "substances": [
      { "componentId": "bottle", "element": "Y", "form": "2-ethylhexanoate", "cas": "136-25-4",
        "particleSize": null, "solvent": null, "preferred": true, "tier": "C",
        "rationale": "excluded per operator: line overlap",
        "citations": [{ "source": "catalog", "reference": "ref-catalog/product|Y|x", "retrievedAt": "t" }] } ] }
    """;

    private const string ValidRegulatory = """
    { "dimensions": [
      { "dimension": "ElementGate", "status": "Pass",
        "citations": [{ "source": "regulatory", "reference": "reach/xvii", "retrievedAt": "t" }],
        "confidence": 0.8, "rationale": "not listed" },
      { "dimension": "ApplicationCheck", "status": "Pass",
        "citations": [{ "source": "regulatory", "reference": "ppwr/1", "retrievedAt": "t" }],
        "confidence": 0.8, "rationale": "permitted" },
      { "dimension": "Hazard", "status": "Pass",
        "citations": [{ "source": "sds", "reference": "sds/136-25-4", "retrievedAt": "t" }],
        "confidence": 0.8, "rationale": "no CMR" } ] }
    """;

    [Fact]
    public async Task Discovery_WithoutARevision_SendsTheOrdinaryPrompt()
    {
        var agent = new ScriptedAgent(ValidDiscovery);

        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), revision: null, default);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("REVISION", agent.Received[0]);
    }

    [Fact]
    public async Task Discovery_WithARevision_CarriesTheOperatorsTargetAndReasonIntoThePrompt()
    {
        var agent = new ScriptedAgent(ValidDiscovery);

        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), Revision(Stages.Discovery), default);

        Assert.True(result.Succeeded);
        var prompt = agent.Received[0];
        Assert.Contains("REVISION", prompt);
        Assert.Contains(Target, prompt);
        Assert.Contains(Reason, prompt);
    }

    [Fact]
    public async Task Regulatory_WithoutARevision_SendsTheOrdinaryPrompt()
    {
        var agent = new ScriptedAgent(ValidRegulatory);

        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), revision: null, default);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("REVISION", agent.Received[0]);
    }

    [Fact]
    public async Task Regulatory_WithARevision_CarriesTheOperatorsTargetAndReasonIntoThePrompt()
    {
        var agent = new ScriptedAgent(ValidRegulatory);

        var result = await RegulatoryAgent.RunAsync(
            agent, Constraints(), Candidate(), Revision(Stages.Regulatory), default);

        Assert.True(result.Succeeded);
        var prompt = agent.Received[0];
        Assert.Contains("REVISION", prompt);
        Assert.Contains(Target, prompt);
        Assert.Contains(Reason, prompt);
    }
}
