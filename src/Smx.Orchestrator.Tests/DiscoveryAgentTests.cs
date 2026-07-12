using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DiscoveryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Y", "Kα", "V", null)],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private const string Valid = """
    { "substances": [
      { "componentId": "bottle", "element": "Y", "form": "2-ethylhexanoate", "cas": "136-25-4",
        "particleSize": null, "solvent": "mineral spirits", "preferred": true, "tier": "A",
        "rationale": "clean XRF (V), catalog-available",
        "citations": [{ "source": "catalog", "reference": "ref-catalog/product|Y|x", "retrievedAt": "t" }] } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesCandidatesDoc()
    {
        var result = await DiscoveryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|candidates", result.Output!.Id);
        Assert.Single(result.Output.Substances);
        Assert.Equal("A", result.Output.Substances[0].Tier);
    }

    [Fact]
    public async Task Candidate_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"componentId\": \"bottle\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task Candidate_WithElementNotInPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Cd\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("not in the element pool", result.Error);
    }

    [Fact]
    public async Task Candidate_WithoutCitation_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"catalog\", \"reference\": \"ref-catalog/product|Y|x\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }
}
