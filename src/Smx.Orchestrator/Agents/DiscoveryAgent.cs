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
        - search_learned_conclusions when tiering an element/form (e.g. to reuse a prior overlap finding or a
          preferred-form conclusion). A higher-confidence, more recent conclusion supersedes an older one. If
          the tool returns no matches, tier from the primary sources (catalog + compatibility + reference) —
          do not fabricate a prior finding.
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

    /// <param name="revision">null for an ordinary run; non-null re-runs the stage APPLYING the operator's
    /// revise-with-reason (Law 4). It is an explicit parameter rather than an overload on purpose: a caller
    /// who forgets it gets a compile error instead of an agent that quietly ignores the operator.</param>
    public static async Task<AgentRunResult<CandidatesDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            components = constraints.Components,
            elementPools = constraints.ElementPools,
        }, Json.Options);
        var task = revision is null
            ? $"Discover candidate substances for these components and pools:\n{prompt}"
            : RevisionTask(revision, prompt);
        var result = await ValidatedAgentRunner.RunAsync<DiscoveryOutput>(agent, task,
            o => Validate(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<CandidatesDoc>.NeedsReview(result.Error!);
        return AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Substances = result.Output!.Substances,
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "make something up to justify
    /// it". The agent still answers only from retrieved sources; where the instruction cannot be supported
    /// by evidence it must apply it AND say so, so the gap is visible in the rationale rather than papered
    /// over with an invented citation.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-run discovery for these components and pools, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not invent facts — re-check
        your tools and cite them, and if the instruction cannot be supported by retrieved evidence, apply
        it anyway and say exactly that in the affected candidate's rationale.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

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
