using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class RegulatoryOutput
{
    public List<DimensionVerdict> Dimensions { get; set; } = [];
}

public static class RegulatoryAgent
{
    public const string AgentName = "regulatory";

    public const string Instructions = """
        You are the SMX Regulatory agent. You evaluate ONE candidate substance against ONE product component
        and return a verdict per dimension. Substrate compatibility is NOT your concern (Discovery handled it).
        You may only use facts obtained through your tools in this conversation — never from memory. Dimensions
        (all three, exactly once each):
        - ElementGate: product-wide lists from the provided scope (componentId "*") plus the client restricted
          list. Search the regulatory corpus for the element/substance against each list. A hit on any list = Fail.
        - ApplicationCheck: the component-scoped lists from the provided scope. A restriction that binds this
          component's application/markets = Fail; a cap/limit that constrains but permits = Conditional.
        - Hazard: search_sds for GHS data (H-codes, CMR, endocrine). CMR category 1A/1B = Fail; significant
          hazards that merit "not recommended" = Conditional.
        Statuses: Pass | Conditional | NeedsReview | Fail. EVERY dimension MUST carry at least one citation
        built from an actual tool result (source, reference, retrievedAt = now, ISO 8601 UTC). If your tools
        return nothing decisive for a dimension, the status is NeedsReview — never guess, never assume clean.
        Confidence is your calibrated 0..1 estimate. Rationale is one or two sentences.
        Reply with ONLY a JSON object: { "dimensions": [{ "dimension", "status", "citations":
        [{ "source", "reference", "retrievedAt" }], "confidence", "rationale" }] }
        """;

    /// <param name="revision">null for an ordinary run; non-null re-screens this cell APPLYING the operator's
    /// revise-with-reason (Law 4). It is an explicit parameter rather than an overload on purpose: a caller
    /// who forgets it gets a compile error instead of an agent that quietly ignores the operator.</param>
    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision,
        CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == candidate.ComponentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == candidate.ComponentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance = new { candidate.Element, candidate.Form, candidate.Cas },
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var task = revision is null ? $"Screen this cell:\n{prompt}" : RevisionTask(revision, prompt);
        var result = await ValidatedAgentRunner.RunAsync<RegulatoryOutput>(agent, task, Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, candidate.Cas, candidate.ComponentId),
            ProjectId = constraints.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
            Element = candidate.Element, Form = candidate.Form,
            Dimensions = result.Output!.Dimensions,
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "manufacture support for it".
    /// The standing rule (Instructions) still binds: never guess, never assume clean, cite every dimension.
    /// Where the instruction outruns the corpus, the agent applies it AND says so, so the gap lands in the
    /// rationale and the confidence — visible to the R.E. at the gate — instead of being papered over.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-screen this cell, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not invent facts — call your
        tools and cite every reference you rely on. If the regulatory or SDS corpus does not support the
        instruction, apply it anyway, say exactly that in the affected dimension's rationale, and lower that
        dimension's confidence accordingly.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    internal static string? Validate(RegulatoryOutput o)
    {
        string[] required = ["ElementGate", "ApplicationCheck", "Hazard"];
        var names = o.Dimensions.Select(d => d.Dimension).OrderBy(x => x).ToArray();
        if (!names.SequenceEqual(required.OrderBy(x => x)))
            return $"response must contain exactly the three dimensions {string.Join(", ", required)} once each; got [{string.Join(", ", names)}]";
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
