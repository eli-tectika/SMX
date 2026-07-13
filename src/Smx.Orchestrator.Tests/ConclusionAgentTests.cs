using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ConclusionAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Ba", "Kα", "V", null), new("bottle", "Y", "Kα", "V", null)],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private static RevisionDoc Revision() => new()
    {
        Id = RecordIds.Revision("p1", Stages.Discovery, "r1"), ProjectId = "p1", Stage = Stages.Discovery,
        Target = "move Ba to tier C",
        // ASCII on purpose: the prompt is JSON-serialized with Json.Options (Web defaults), whose encoder
        // escapes non-ASCII, so a Greek "Kα" would reach the model as "Kα" — verbatim, but not a
        // substring of the prompt text. Asserting the reason survives is the point; fighting the encoder is not.
        Reason = "Ba Ka overlaps the Ti Kb line in this HDPE background",
        CreatedAt = "2026-07-13T00:00:00.0000000+00:00",
    };

    private const string Valid = """
    { "scope": { "element": "Ba", "form": null, "material": "HDPE", "application": null, "market": null,
      "substance": null },
      "finding": "Barium is unsuitable for XRF-marked HDPE where Ti is present: the Ba Ka line overlaps Ti Kb.",
      "confidence": 0.6 }
    """;

    [Fact]
    public async Task ValidOutput_IsAccepted()
    {
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(Valid), Revision(), Constraints(), "{\"substances\":[]}", default);

        Assert.True(result.Succeeded);
        Assert.Equal("Ba", result.Output!.Scope.Element);
        Assert.Equal("HDPE", result.Output.Scope.Material);
        Assert.Contains("Barium", result.Output.Finding);
        Assert.Equal(0.6, result.Output.Confidence);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task EmptyFinding_IsRejected(string finding)
    {
        var bad = Valid.Replace(
            "\"finding\": \"Barium is unsuitable for XRF-marked HDPE where Ti is present: the Ba Ka line overlaps Ti Kb.\"",
            $"\"finding\": {finding}");
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(bad, bad, bad), Revision(), Constraints(), "{}", default);

        Assert.False(result.Succeeded);
        Assert.Contains("finding is required", result.Error);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task ConfidenceOutsideZeroToOne_IsRejected(double confidence)
    {
        var bad = Valid.Replace("\"confidence\": 0.6", $"\"confidence\": {confidence}");
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(bad, bad, bad), Revision(), Constraints(), "{}", default);

        Assert.False(result.Succeeded);
        Assert.Contains("confidence must be between", result.Error);
    }

    /// The no-fabrication guard. A Learned Conclusion is evidence a FUTURE, unrelated project will act on,
    /// so a conclusion scoped to an element this project never touched means the model invented the very
    /// subject of the finding — and the invention would then be filed as cross-project knowledge.
    [Fact]
    public async Task ScopeElement_TheProjectNeverContained_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Ba\"", "\"element\": \"Cd\"");
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(bad, bad, bad), Revision(), Constraints(), "{}", default);

        Assert.False(result.Succeeded);
        Assert.Contains("not an element in this project", result.Error);
    }

    /// Not every revision constrains a dimension, and an over-narrow scope HIDES the conclusion from the
    /// projects that need it. An all-null scope is a legitimate, maximally-reusable conclusion.
    [Fact]
    public async Task AllNullScope_IsAccepted()
    {
        var allNull = """
        { "scope": { "element": null, "form": null, "material": null, "application": null, "market": null,
          "substance": null },
          "finding": "Any element whose K line overlaps a background line must be excluded, not down-tiered.",
          "confidence": 0.5 }
        """;
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(allNull), Revision(), Constraints(), "{}", default);

        Assert.True(result.Succeeded);
        Assert.Null(result.Output!.Scope.Element);
        Assert.Null(result.Output.Scope.Material);
    }

    /// The instructions tell the model to "leave the rest null", so a model dropping the scope object
/// entirely is a response we invited. It must be treated as the (legal) all-null scope and never crash
/// the validator: an unhandled NRE escapes ValidatedAgentRunner (it catches only JsonException), so it
/// would fail the whole stage instead of costing one retry.
    [Theory]
    [InlineData("\"scope\": null,")]
    [InlineData("")]
    public async Task MissingOrNullScope_IsTreatedAsAnAllNullScope(string scopeJson)
    {
        var json = $$"""
        { {{scopeJson}}
          "finding": "Elements whose K line overlaps a background line must be excluded, not down-tiered.",
          "confidence": 0.5 }
        """;
        var result = await ConclusionAgent.RunAsync(
            new ScriptedAgent(json), Revision(), Constraints(), "{}", default);

        Assert.True(result.Succeeded);
        Assert.Null(result.Output!.Scope.Element);
    }

    /// The distiller must SEE the operator's reason — it is the entire substance of the conclusion.
    [Fact]
    public async Task ThePrompt_CarriesTheOperatorsTargetAndReason()
    {
        var agent = new ScriptedAgent(Valid);
        await ConclusionAgent.RunAsync(agent, Revision(), Constraints(), "{}", default);

        var prompt = agent.Received[0];
        Assert.Contains("Ba Ka overlaps the Ti Kb line in this HDPE background", prompt);
        Assert.Contains("move Ba to tier C", prompt);
    }
}
