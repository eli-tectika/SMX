using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class PoolAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand", null, "solid")],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private const string Valid = """
    { "suggestions": [
      { "component": "bottle", "element": "Zr", "formClass": "compound",
        "rationale": "an oxide suits a solid polymer; from general chemistry knowledge",
        "citations": [] } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesPoolDoc()
    {
        var result = await PoolAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), null, default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|pool", result.Output!.Id);
        Assert.Equal("agent", result.Output.Source);
        Assert.Equal("Zr", Assert.Single(result.Output.Suggestions).Element);
    }

    // The load-bearing difference from Discovery: a suggestion may rest on model knowledge alone, so an empty
    // citation list is VALID here (Discovery would reject a candidate with no cited source).
    [Fact]
    public async Task Suggestion_WithNoCitations_IsAccepted()
    {
        var result = await PoolAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), null, default);
        Assert.True(result.Succeeded);
        Assert.Empty(Assert.Single(result.Output!.Suggestions).Citations);
    }

    [Fact]
    public async Task Suggestion_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"component\": \"bottle\"", "\"component\": \"lid\"");
        var result = await PoolAgent.RunAsync(new ScriptedAgent(bad, bad, bad), Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task Suggestion_WithBadFormClass_IsRejected()
    {
        var bad = Valid.Replace("\"formClass\": \"compound\"", "\"formClass\": \"nanoparticle\"");
        var result = await PoolAgent.RunAsync(new ScriptedAgent(bad, bad, bad), Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("formClass", result.Error);
    }

    [Fact]
    public async Task EmptySuggestions_IsRejected()
    {
        const string bad = """{ "suggestions": [] }""";
        var result = await PoolAgent.RunAsync(new ScriptedAgent(bad, bad, bad), Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("at least one", result.Error);
    }

    [Fact]
    public async Task Suggestion_WithNoRationale_IsRejected()
    {
        var bad = Valid.Replace(
            "\"rationale\": \"an oxide suits a solid polymer; from general chemistry knowledge\"",
            "\"rationale\": \"\"");
        var result = await PoolAgent.RunAsync(new ScriptedAgent(bad, bad, bad), Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("rationale", result.Error);
    }
}
