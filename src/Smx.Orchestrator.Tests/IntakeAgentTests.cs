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

    [Fact]
    public async Task AlteredProvidedCandidate_IsRejected()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [],
          "providedCandidates": [{ "componentId": "bottle", "element": "Y", "form": "2-EH", "cas": "136-25-4", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "provided", "citations": [] }],
          "clientRestrictedList": [] }
        """).RootElement;
        var project = ProjectDoc.Create("p1", "Acme", "MUFE", payload);
        var bad = """
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [],
          "providedCandidates": [{ "componentId": "bottle", "element": "Y", "form": "2-EH", "cas": "999-99-9", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "provided", "citations": [] }],
          "clientRestrictedList": [],
          "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate", "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
        """;
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, project, default);
        Assert.False(result.Succeeded);
        Assert.Contains("provided candidates must exactly echo", result.Error);
    }

    [Fact]
    public async Task ScopeEntry_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"componentId\": \"*\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task ScopeEntry_WithoutCitation_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citation\": { \"source\": \"regulatory\", \"reference\": \"regulatory-index/reach-17\", \"retrievedAt\": \"t\" }",
            "\"citation\": { \"source\": \"\", \"reference\": \"\", \"retrievedAt\": \"t\" }");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }
}
