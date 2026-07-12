using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class IntakeOutput
{
    public List<ComponentSpec> Components { get; set; } = [];
    public List<ElementPool> ElementPools { get; set; } = [];
    public List<CandidateSubstance> ProvidedCandidates { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    public List<AppliedList> DerivedScope { get; set; } = [];
}

public static class IntakeAgent
{
    public const string AgentName = "constraint-intake";

    public const string Instructions = """
        You are the SMX Constraint-Intake agent. You receive a project's raw constraints payload and must
        normalize it and DERIVE the regulatory scope. You never invent data: components, element pools,
        provided candidates and the client restricted list must EXACTLY echo the input. Your added value is
        `derivedScope`:
        - The product-wide element gate lists ALWAYS apply (componentId "*"): REACH Annex XVII, RoHS (if
          electronics), PPWR heavy-metal cap (if packaging), SVHC, Prop 65 (if US market), client restricted list.
        - Per-component application lists follow from application × target markets (e.g. EU Cosmetics for a
          skin-contact liquid in EU, migration/SML if food-contact, FDA regimes for US market).
        Use the search_regulatory tool to confirm each list applies and cite the retrieved reference in that
        entry's citation (source = the tool's source, reference = the returned reference id, retrievedAt = now,
        ISO 8601 UTC). Every derivedScope entry MUST carry a citation from an actual tool result. If retrieval
        gives you nothing for a list you believe applies, do not include it silently — include it only with a
        real citation, otherwise leave it out.
        Reply with ONLY a JSON object of shape:
        { "components": [...], "elementPools": [...], "providedCandidates": [...], "clientRestrictedList": [...],
          "derivedScope": [{ "listId", "componentId" ("*" for product-wide), "reason",
                             "citation": { "source", "reference", "retrievedAt" } }] }
        """;

    public static async Task<AgentRunResult<ConstraintsDoc>> RunAsync(ISmxAgent agent, ProjectDoc project, CancellationToken ct)
    {
        var prompt = $"Project constraints payload:\n{JsonSerializer.Serialize(project.Payload, Json.Options)}";
        var result = await ValidatedAgentRunner.RunAsync<IntakeOutput>(agent, prompt, o => Validate(o, project), ct);
        if (!result.Succeeded) return AgentRunResult<ConstraintsDoc>.NeedsReview(result.Error!);
        var o = result.Output!;
        return AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(project.ProjectId), ProjectId = project.ProjectId,
            Components = o.Components, ElementPools = o.ElementPools,
            ProvidedCandidates = o.ProvidedCandidates,
            ClientRestrictedList = o.ClientRestrictedList, DerivedScope = o.DerivedScope,
        });
    }

    internal static string? Validate(IntakeOutput o, ProjectDoc project)
    {
        var payload = JsonSerializer.Deserialize<IntakeOutput>(project.Payload.GetRawText(), Json.Options)!;
        if (o.Components.Count != payload.Components.Count ||
            !o.Components.Select(c => c.Id).OrderBy(x => x).SequenceEqual(payload.Components.Select(c => c.Id).OrderBy(x => x)))
            return "components must exactly echo the input payload (no additions/removals)";
        static IEnumerable<string> Keys(IEnumerable<ElementPool> ps) =>
            ps.Select(p => $"{p.Component}|{p.Element}|{p.Line}").OrderBy(x => x);
        if (!Keys(o.ElementPools).SequenceEqual(Keys(payload.ElementPools)))
            return "element pools must exactly echo the input payload (no additions/removals)";
        if (o.DerivedScope.Count == 0)
            return "derivedScope must not be empty — at minimum the product-wide element gate lists apply";
        var known = o.Components.Select(c => c.Id).Append("*").ToHashSet();
        foreach (var e in o.DerivedScope)
        {
            if (!known.Contains(e.ComponentId)) return $"derivedScope references unknown component '{e.ComponentId}'";
            if (string.IsNullOrWhiteSpace(e.Citation?.Source) || string.IsNullOrWhiteSpace(e.Citation?.Reference))
                return $"derivedScope entry '{e.ListId}' is missing its citation — every list must cite a retrieved source";
        }
        return null;
    }
}
