using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class IntakeAgentTests
{
    private static ProjectDoc Project()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""
        {
          "client": "Acme", "product": "Shampoo bottle",
          "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "substances": [{ "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }],
          "clientRestrictedList": ["Pb"]
        }
        """);
        return ProjectDoc.Create("p1", "Acme", "Shampoo bottle", payload);
    }

    private const string ValidResponse = """
    {
      "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
      "substances": [{ "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }],
      "clientRestrictedList": ["Pb"],
      "derivedScope": [
        { "listId": "reach-annex-xvii", "componentId": "*", "reason": "element gate always applies in EU",
          "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "2026-07-08T00:00:00Z" } },
        { "listId": "ppwr-heavy-metals", "componentId": "bottle", "reason": "packaging application, EU market",
          "citation": { "source": "regulatory", "reference": "regulatory-index/ppwr-1", "retrievedAt": "2026-07-08T00:00:00Z" } }
      ]
    }
    """;

    [Fact]
    public async Task ValidResponse_BecomesConstraintsDoc()
    {
        var result = await IntakeAgent.RunAsync(new ScriptedAgent(ValidResponse), Project(), default);
        Assert.True(result.Succeeded);
        var doc = result.Output!;
        Assert.Equal(RecordIds.Constraints("p1"), doc.Id);
        Assert.Equal(2, doc.DerivedScope.Count);
        Assert.Equal("*", doc.DerivedScope[0].ComponentId);
    }

    [Fact]
    public async Task ScopeEntry_ForUnknownComponent_IsRejected_ThenRetried()
    {
        var bad = ValidResponse.Replace("\"componentId\": \"bottle\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.True(result.Succeeded);
        Assert.Contains("unknown component", agent.Received[1]);
    }

    [Fact]
    public async Task ScopeEntry_WithoutCitation_IsRejected()
    {
        var bad = ValidResponse.Replace("\"source\": \"regulatory\"", "\"source\": \"\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded); // 3 attempts, all uncited → needs review
    }

    [Fact]
    public async Task SubstancesMustEchoInput_NoInventedCandidates()
    {
        var bad = ValidResponse.Replace("39049-04-2", "999-99-9");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.True(result.Succeeded);
        Assert.Contains("must exactly echo", agent.Received[1]);
    }
}
