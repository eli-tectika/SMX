using Smx.Domain.Records;
using Smx.Backend.Agents;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// Discovery's FIRST PASS — the pool (was PoolAgent, merged 2026-08-06, redesign spec §16.3).
///
/// Everything asserted here is SHAPE. None of it can tell a good element from a bad one, and none of it
/// tries: the ~10-per-component target is judgement and lives in the prompt, because a hard minimum in
/// Validate would be met by padding the list, and an invented element is worse than a short pool.
public class DiscoveryPoolPassTests
{
    private static ConstraintsDoc Constraints(params string[] components) => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [.. (components.Length == 0 ? ["bottle"] : components)
            .Select(id => new ComponentSpec(id, "PET", "packaging", ["EU"], "brand", null, "solid"))],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private const string Valid = """
    { "elements": [
        { "component": "bottle", "element": "Zr",
          "rationale": "Kalpha is clean against PET's expected background", "citations": [] } ],
      "suggestions": [
        { "component": "bottle", "element": "Zr", "formClass": "compound",
          "rationale": "an oxide suits a solid polymer; from general chemistry knowledge",
          "citations": [] } ] }
    """;

    private const string BothComponents = """
    { "elements": [
        { "component": "bottle", "element": "Zr", "rationale": "clean on PET", "citations": [] },
        { "component": "lid", "element": "Y", "rationale": "clean on the closure resin", "citations": [] } ],
      "suggestions": [
        { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide on a solid polymer", "citations": [] },
        { "component": "lid", "element": "Y", "formClass": "compound", "rationale": "oxide on a solid polymer", "citations": [] } ] }
    """;

    private static Task<AgentRunResult<PoolDoc>> Run(string response, ConstraintsDoc? c = null) =>
        // Three identical responses: a rejected output is retried twice before the run gives up, and a
        // one-response script would let the second attempt "succeed" on a stale reply.
        DiscoveryAgent.RunPoolAsync(new ScriptedAgent(response, response, response), c ?? Constraints(), null, default);

    [Fact]
    public async Task ValidResponse_BecomesPoolDoc_WithBothSteps()
    {
        var result = await Run(Valid);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|pool", result.Output!.Id);
        Assert.Equal("agent", result.Output.Source);
        Assert.Equal("Zr", Assert.Single(result.Output.Elements).Element);
        Assert.Equal("Zr", Assert.Single(result.Output.Suggestions).Element);
    }

    // The load-bearing difference from the corroboration pass: a proposal may rest on model knowledge alone,
    // so an empty citation list is VALID here (Validate would reject a candidate with no cited source).
    [Fact]
    public async Task Suggestion_WithNoCitations_IsAccepted()
    {
        var result = await Run(Valid);
        Assert.True(result.Succeeded);
        Assert.Empty(Assert.Single(result.Output!.Suggestions).Citations);
    }

    // ONE AGENT. The pool runs under the Discovery agent's name — there is no second agent to name.
    [Fact]
    public void PoolPass_IsTheDiscoveryAgent()
    {
        Assert.Equal("discovery", DiscoveryAgent.AgentName);
        Assert.NotEmpty(DiscoveryAgent.PoolInstructions);
        Assert.NotEqual(DiscoveryAgent.Instructions, DiscoveryAgent.PoolInstructions);
        // The target is interpolated from the constant, not typed twice — a literal in the prompt would
        // drift from the number the task states and nothing would notice.
        Assert.Contains($"about {DiscoveryAgent.TargetElementsPerComponent} elements FOR EACH COMPONENT",
            DiscoveryAgent.PoolInstructions);
    }

    // THE TARGET BREADTH IS PER COMPONENT, AND IT IS STATED PER RUN. The prompt named no number at all
    // before this, which is how a real run came back with three candidates for one component and two for
    // another. Asserted on the TASK the agent is actually handed — an instruction constant nobody sends is
    // not a prompt.
    [Fact]
    public async Task Task_StatesTheTarget_PerComponent_NotPerProject()
    {
        var agent = new ScriptedAgent(BothComponents);
        await DiscoveryAgent.RunPoolAsync(agent, Constraints("bottle", "lid"), null, default);

        var task = Assert.Single(agent.Received);
        Assert.Contains(DiscoveryAgent.TargetElementsPerComponent.ToString(), task);
        Assert.Contains("EACH component", task);
        Assert.Contains("not across the project", task);
        Assert.Contains("2 components", task);
    }

    // EVERY COMPONENT GETS A POOL. Components are marked independently, so a component nothing is proposed
    // for leaves the analysis silently: Discovery only corroborates what the pool proposed, and the record
    // would report a finished project with a hole in it. A needs-review naming the component is the loud
    // reading; an empty track is the quiet one.
    [Fact]
    public async Task Pool_CoveringOnlyOneOfTwoComponents_IsRejected()
    {
        var result = await Run(Valid, Constraints("bottle", "lid"));
        Assert.False(result.Succeeded);
        Assert.Contains("'lid'", result.Error);
        Assert.Contains("no proposed markers", result.Error);
    }

    [Fact]
    public async Task Pool_CoveringEveryComponent_IsAccepted()
    {
        var result = await Run(BothComponents, Constraints("bottle", "lid"));
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Output!.Suggestions.Select(s => s.Component).Distinct().Count());
    }

    // SUGGESTIONS ARE GROUPED PER COMPONENT — the same element may be proposed for two components and each
    // is its own track, so neither may swallow the other.
    [Fact]
    public async Task TheSameElement_ForTwoComponents_IsTwoSuggestions()
    {
        const string shared = """
        { "elements": [
            { "component": "bottle", "element": "Zr", "rationale": "clean on PET", "citations": [] },
            { "component": "lid", "element": "Zr", "rationale": "clean on the closure resin", "citations": [] } ],
          "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] },
            { "component": "lid", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] } ] }
        """;
        var result = await Run(shared, Constraints("bottle", "lid"));
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Output!.Suggestions.Count);
        Assert.Equal(["bottle", "lid"], result.Output.Suggestions.Select(s => s.Component).Order());
    }

    // ELEMENTS FIRST. The two steps ask different questions of different evidence; if step 2 can name an
    // element step 1 never chose, the pass has collapsed back into the single "element + form" draft this
    // split exists to undo — and nothing in the output would show it.
    [Fact]
    public async Task Suggestion_ForAnElementThatWasNeverChosen_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Zr\", \"formClass\"", "\"element\": \"Hf\", \"formClass\"");
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("not in your \"elements\" list", result.Error);
    }

    // …and the mirror: an element chosen and then never broken into a form is step 1's answer dropped on the
    // way to step 2. It reads as a considered element in the artifact and produces nothing.
    [Fact]
    public async Task ChosenElement_WithNoForm_IsRejected()
    {
        const string unbroken = """
        { "elements": [
            { "component": "bottle", "element": "Zr", "rationale": "clean on PET", "citations": [] },
            { "component": "bottle", "element": "Y", "rationale": "clean on PET too", "citations": [] } ],
          "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] } ] }
        """;
        var result = await Run(unbroken);
        Assert.False(result.Succeeded);
        Assert.Contains("'Y' in 'bottle'", result.Error);
        Assert.Contains("broken into no form", result.Error);
    }

    [Fact]
    public async Task MissingElementsStep_IsRejected()
    {
        const string formsOnly = """
        { "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] } ] }
        """;
        var result = await Run(formsOnly);
        Assert.False(result.Succeeded);
        Assert.Contains("\"elements\" step is missing", result.Error);
    }

    // Breadth is a COUNT, so a repeated (component, element, form-class) is breadth on paper only.
    [Fact]
    public async Task DuplicateSuggestion_IsRejected()
    {
        const string dup = """
        { "elements": [
            { "component": "bottle", "element": "Zr", "rationale": "clean on PET", "citations": [] } ],
          "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] },
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide again", "citations": [] } ] }
        """;
        var result = await Run(dup);
        Assert.False(result.Succeeded);
        Assert.Contains("listed twice", result.Error);
    }

    // A second form of an ALREADY-CHOSEN element is exactly what step 2 is for, so it must not be caught by
    // the duplicate rail.
    [Fact]
    public async Task TwoFormsOfOneElement_AreAccepted()
    {
        const string twoForms = """
        { "elements": [
            { "component": "bottle", "element": "Zr", "rationale": "clean on PET", "citations": [] } ],
          "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] },
            { "component": "bottle", "element": "Zr", "formClass": "organocomplex", "rationale": "if a masterbatch carrier is used", "citations": [] } ] }
        """;
        var result = await Run(twoForms);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Output!.Suggestions.Count);
    }

    [Fact]
    public async Task Suggestion_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"component\": \"bottle\", \"element\": \"Zr\", \"formClass\"",
                                "\"component\": \"cap\", \"element\": \"Zr\", \"formClass\"");
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task ElementChoice_ForUnknownComponent_IsRejected()
    {
        const string bad = """
        { "elements": [
            { "component": "cap", "element": "Zr", "rationale": "clean", "citations": [] } ],
          "suggestions": [
            { "component": "bottle", "element": "Zr", "formClass": "compound", "rationale": "oxide", "citations": [] } ] }
        """;
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task Suggestion_WithBadFormClass_IsRejected()
    {
        var bad = Valid.Replace("\"formClass\": \"compound\"", "\"formClass\": \"nanoparticle\"");
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("formClass", result.Error);
    }

    [Fact]
    public async Task EmptySuggestions_IsRejected()
    {
        const string bad = """
        { "elements": [ { "component": "bottle", "element": "Zr", "rationale": "clean", "citations": [] } ],
          "suggestions": [] }
        """;
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("at least one", result.Error);
    }

    [Fact]
    public async Task Suggestion_WithNoRationale_IsRejected()
    {
        var bad = Valid.Replace(
            "\"rationale\": \"an oxide suits a solid polymer; from general chemistry knowledge\"",
            "\"rationale\": \"\"");
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("must name why it suits the substrate", result.Error);
    }

    [Fact]
    public async Task ElementChoice_WithNoRationale_IsRejected()
    {
        var bad = Valid.Replace("\"rationale\": \"Kalpha is clean against PET's expected background\"",
                                "\"rationale\": \"\"");
        var result = await Run(bad);
        Assert.False(result.Succeeded);
        Assert.Contains("why it is detectable and clean", result.Error);
    }

    // The revision path must still work the two steps — an operator's "add hafnium" that came back as forms
    // with no element reasoning would be the collapse re-entering through the back door.
    [Fact]
    public async Task RevisionTask_RestatesBothSteps()
    {
        var agent = new ScriptedAgent(Valid);
        var revision = new RevisionDoc
        {
            Id = "p1|revision|pool|k", ProjectId = "p1", Stage = Stages.Pool,
            Target = "Zr", Reason = "the customer's line already runs a zirconium additive",
            CreatedAt = "2026-08-06T00:00:00.0000000Z",
        };
        await DiscoveryAgent.RunPoolAsync(agent, Constraints(), revision, default);

        var task = Assert.Single(agent.Received);
        Assert.Contains("the customer's line already runs a zirconium additive", task);
        Assert.Contains("elements for every component first", task);
    }
}
