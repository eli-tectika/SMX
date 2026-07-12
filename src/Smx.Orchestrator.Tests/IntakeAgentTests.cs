using System.Text.Json;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class IntakeAgentTests
{
    private static ProjectDoc Project()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
          "providedCandidates": [],
          "clientRestrictedList": ["Pb"] }
        """).RootElement;
        return ProjectDoc.Create("p1", "Acme", "MUFE", payload);
    }

    private const string Valid = """
    { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
      "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
      "providedCandidates": [],
      "clientRestrictedList": ["Pb"],
      "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate",
        "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesConstraintsDoc_WithElementPools()
    {
        var result = await IntakeAgent.RunAsync(new ScriptedAgent(Valid), Project(), default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.ElementPools);
        Assert.Equal("Y", result.Output.ElementPools[0].Element);
    }

    [Fact]
    public async Task AlteredElementPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Zr\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("element pools must exactly echo", result.Error);
    }
}
