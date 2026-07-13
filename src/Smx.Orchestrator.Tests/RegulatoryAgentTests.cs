using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class RegulatoryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "element gate",
            new Citation("regulatory", "regulatory-index/reach-17", "t"))],
    };

    private static CandidateSubstance Candidate() =>
        new("bottle", "Cd", "sulfide", "1306-23-6", null, null, true, "A", "provided", []);

    private const string Valid = """
    { "dimensions": [
      { "dimension": "ElementGate", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-e23", "retrievedAt": "t" }],
        "confidence": 0.98, "rationale": "Cd restricted by REACH Annex XVII entry 23" },
      { "dimension": "ApplicationCheck", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr-hm", "retrievedAt": "t" }],
        "confidence": 0.95, "rationale": "PPWR heavy-metal cap" },
      { "dimension": "Hazard", "status": "Fail",
        "citations": [{ "source": "sds", "reference": "sds-index/cd-ghs", "retrievedAt": "t" }],
        "confidence": 0.97, "rationale": "carcinogenic H350" } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesVerdictDoc_ThreeDimensions()
    {
        var result = await RegulatoryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), Candidate(), null, default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|verdict|1306-23-6|bottle", result.Output!.Id);
        Assert.Equal(VerdictStatus.Fail, result.Output.Overall);
        Assert.Equal(3, result.Output.Dimensions.Count);
    }

    [Fact]
    public async Task IncludingCompatibilityDimension_IsRejected()
    {
        var bad = Valid.Replace("\"dimension\": \"Hazard\"", "\"dimension\": \"Compatibility\"");
        var agent = new ScriptedAgent(bad, Valid);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        Assert.True(result.Succeeded);
        Assert.Contains("exactly the three dimensions", agent.Received[1]);
    }

    [Fact]
    public async Task UncitedDimension_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"sds\", \"reference\": \"sds-index/cd-ghs\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }

    [Fact]
    public async Task PromptCarriesCandidate_ScopeAndRestrictedList()
    {
        var agent = new ScriptedAgent(Valid);
        await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        var prompt = agent.Received[0];
        Assert.Contains("1306-23-6", prompt);
        Assert.Contains("reach-annex-xvii", prompt);
        Assert.Contains("Pb", prompt);
    }
}
