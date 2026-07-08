using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ScreeningAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        Substances = [new("Cd", "sulfide", "1306-23-6")],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "element gate",
            new Citation("regulatory", "regulatory-index/reach-17", "t"))],
    };

    private const string ValidResponse = """
    {
      "dimensions": [
        { "dimension": "Compatibility", "status": "Pass",
          "citations": [{ "source": "reference", "reference": "ref-compatibility/cd-hdpe", "retrievedAt": "t" }],
          "confidence": 0.9, "rationale": "tabulated compatible" },
        { "dimension": "ElementGate", "status": "Fail",
          "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-e23", "retrievedAt": "t" }],
          "confidence": 0.98, "rationale": "Cd restricted by REACH Annex XVII entry 23" },
        { "dimension": "ApplicationCheck", "status": "Fail",
          "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr-hm", "retrievedAt": "t" }],
          "confidence": 0.95, "rationale": "PPWR heavy-metal cap" },
        { "dimension": "Hazard", "status": "Fail",
          "citations": [{ "source": "sds", "reference": "sds-index/cd-ghs", "retrievedAt": "t" }],
          "confidence": 0.97, "rationale": "carcinogenic H350" }
      ]
    }
    """;

    [Fact]
    public async Task ValidResponse_BecomesVerdictDoc_WithDeterministicId()
    {
        var result = await ScreeningAgent.RunAsync(new ScriptedAgent(ValidResponse), Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|verdict|1306-23-6|bottle", result.Output!.Id);
        Assert.Equal(VerdictStatus.Fail, result.Output.Overall);
        Assert.Equal(4, result.Output.Dimensions.Count);
    }

    [Fact]
    public async Task MissingDimension_IsRejected_ThenRetried()
    {
        var bad = ValidResponse.Replace("\"dimension\": \"Hazard\"", "\"dimension\": \"Compatibility\"");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await ScreeningAgent.RunAsync(agent, Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.True(result.Succeeded);
        Assert.Contains("exactly the four dimensions", agent.Received[1]);
    }

    [Fact]
    public async Task NonFailDimension_WithoutCitation_IsRejected()
    {
        // A Fail without citation is also invalid, but the sharper rule: nothing passes uncited.
        var bad = ValidResponse.Replace(
            "\"citations\": [{ \"source\": \"reference\", \"reference\": \"ref-compatibility/cd-hdpe\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await ScreeningAgent.RunAsync(agent, Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.False(result.Succeeded); // needs_review after 3 uncited attempts
        Assert.Contains("citation", result.Error);
    }

    [Fact]
    public async Task PromptCarriesCell_ScopeAndRestrictedList()
    {
        var agent = new ScriptedAgent(ValidResponse);
        await ScreeningAgent.RunAsync(agent, Constraints(), new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        var prompt = agent.Received[0];
        Assert.Contains("1306-23-6", prompt);
        Assert.Contains("HDPE", prompt);
        Assert.Contains("reach-annex-xvii", prompt);
        Assert.Contains("Pb", prompt); // client restricted list travels with the prompt
    }
}
