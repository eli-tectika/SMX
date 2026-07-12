using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class DiscoveryOutput
{
    public List<CandidateSubstance> Substances { get; set; } = [];
}

public static class DiscoveryAgent
{
    public const string AgentName = "discovery";

    public const string Instructions = """
        You are the SMX Discovery agent. For each component you receive its usable/conditional element POOL
        (V = clean, L = conditional) plus material, application and objective. Turn each pooled element into
        one or more FULLY-SPECIFIED candidate substances: element + molecular form + CAS + (particle size,
        solvent when known). You may only use facts from your tools:
        - search_catalog(element) FIRST — propose only forms/CAS you retrieved there; never invent a CAS.
        - search_reference for solubility / XRF cleanliness / form ranking evidence.
        - lookup_compatibility(element, substrate) as a tiering signal (incompatible ⇒ lower tier or C).
        Rank the forms and set preferred=true on the best one per element×component. Assign a tier with a
        one/two-sentence cited rationale:
        - A: strong (clean signal, catalog-available, no obvious blockers).
        - B: needs validation (e.g. limited use history, single form).
        - C: excluded (present in background, clearly regulated, or substrate-incompatible) — still list it,
          with the reason, so the exclusion is visible.
        EVERY candidate MUST carry at least one citation built from an actual tool result
        (source, reference, retrievedAt = now ISO 8601 UTC). Only propose candidates whose element is in that
        component's pool. Reply with ONLY a JSON object:
        { "substances": [{ "componentId", "element", "form", "cas", "particleSize", "solvent", "preferred",
          "tier" ("A"|"B"|"C"), "rationale", "citations": [{ "source", "reference", "retrievedAt" }] }] }
        """;

    public static async Task<AgentRunResult<CandidatesDoc>> RunAsync(ISmxAgent agent, ConstraintsDoc constraints, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            components = constraints.Components,
            elementPools = constraints.ElementPools,
        }, Json.Options);
        var result = await ValidatedAgentRunner.RunAsync<DiscoveryOutput>(agent,
            $"Discover candidate substances for these components and pools:\n{prompt}",
            o => Validate(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<CandidatesDoc>.NeedsReview(result.Error!);
        return AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Substances = result.Output!.Substances,
        });
    }

    internal static string? Validate(DiscoveryOutput o, ConstraintsDoc constraints)
    {
        if (o.Substances.Count == 0) return "at least one candidate substance is required";
        var componentIds = constraints.Components.Select(c => c.Id).ToHashSet();
        var poolByComponent = constraints.ElementPools
            .GroupBy(p => p.Component)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Element).ToHashSet());
        string[] tiers = ["A", "B", "C"];
        foreach (var s in o.Substances)
        {
            if (!componentIds.Contains(s.ComponentId)) return $"candidate references unknown component '{s.ComponentId}'";
            if (!poolByComponent.TryGetValue(s.ComponentId, out var pool) || !pool.Contains(s.Element))
                return $"candidate element '{s.Element}' is not in the element pool for component '{s.ComponentId}'";
            if (!tiers.Contains(s.Tier)) return $"candidate tier must be one of A|B|C; got '{s.Tier}'";
            if (string.IsNullOrWhiteSpace(s.Cas)) return $"candidate '{s.Element}/{s.Form}' is missing a CAS number";
            if (s.Citations.Count == 0 || s.Citations.Any(c => string.IsNullOrWhiteSpace(c.Source) || string.IsNullOrWhiteSpace(c.Reference)))
                return $"candidate '{s.Element}/{s.Form}' is missing a usable citation — every candidate must cite a retrieved source";
        }
        return null;
    }
}
