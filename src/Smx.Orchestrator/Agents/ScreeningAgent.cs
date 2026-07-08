using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class ScreeningOutput
{
    public List<DimensionVerdict> Dimensions { get; set; } = [];
}

public static class ScreeningAgent
{
    public const string AgentName = "screening";

    public const string Instructions = """
        You are the SMX Screening agent. You evaluate ONE candidate substance against ONE product component
        and return a verdict per dimension. You may only use facts you obtained through your tools in this
        conversation — never from memory. Dimensions (all four, exactly once each):
        - Compatibility: call lookup_compatibility(element, substrate) FIRST. If tabulated, the tabulated
          verdict decides (Pass for compatible, Fail for incompatible, Conditional where the table says
          conditional) and you cite the returned refId. If not tabulated, reason from search_reference
          results with lowered confidence, or return NeedsReview if retrieval is inconclusive.
        - ElementGate: product-wide lists from the provided scope (componentId "*") plus the client
          restricted list. Search the regulatory corpus for the element/substance against each list.
          A hit on any list = Fail.
        - ApplicationCheck: the component-scoped lists from the provided scope. A restriction that binds
          this component's application/markets = Fail; a cap/limit that constrains but permits = Conditional.
        - Hazard: search_sds for GHS data (H-codes, CMR, endocrine). CMR category 1A/1B = Fail;
          significant hazards that merit "not recommended" = Conditional.
        Statuses: Pass | Conditional | NeedsReview | Fail. EVERY dimension MUST carry at least one citation
        built from an actual tool result (source, reference, retrievedAt = now, ISO 8601 UTC). If your tools
        return nothing decisive for a dimension, the status is NeedsReview — never guess, never assume clean.
        Confidence is your calibrated 0..1 estimate. Rationale is one or two sentences.
        Reply with ONLY a JSON object: { "dimensions": [{ "dimension", "status", "citations":
        [{ "source", "reference", "retrievedAt" }], "confidence", "rationale" }] }
        """;

    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == componentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == componentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance,
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var result = await ValidatedAgentRunner.RunAsync<ScreeningOutput>(agent,
            $"Screen this cell:\n{prompt}", Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, substance.Cas, componentId),
            ProjectId = constraints.ProjectId, Cas = substance.Cas, ComponentId = componentId,
            Element = substance.Element, Form = substance.Form,
            Dimensions = result.Output!.Dimensions,
        });
    }

    internal static string? Validate(ScreeningOutput o)
    {
        string[] required = ["Compatibility", "ElementGate", "ApplicationCheck", "Hazard"];
        var names = o.Dimensions.Select(d => d.Dimension).OrderBy(x => x).ToArray();
        if (!names.SequenceEqual(required.OrderBy(x => x)))
            return $"response must contain exactly the four dimensions {string.Join(", ", required)} once each; got [{string.Join(", ", names)}]";
        foreach (var d in o.Dimensions)
        {
            if (d.Citations.Count == 0 || d.Citations.Any(c =>
                    string.IsNullOrWhiteSpace(c.Source) || string.IsNullOrWhiteSpace(c.Reference)))
                return $"dimension '{d.Dimension}' is missing a usable citation — every dimension must cite an actual tool result";
            if (d.Confidence is < 0 or > 1) return $"dimension '{d.Dimension}' confidence must be within 0..1";
            if (string.IsNullOrWhiteSpace(d.Rationale)) return $"dimension '{d.Dimension}' needs a rationale";
        }
        return null;
    }
}
