using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// What the distiller is allowed to author: the three fields that require judgement. Everything an
/// operator's record must not lose — id, kind, provenance, createdAt — is set by code, never here.
public sealed class ConclusionOutput
{
    private static ConclusionScope Unscoped => new(null, null, null, null, null, null);
    private ConclusionScope _scope = Unscoped;

    /// The setter coalesces because we ASKED for this: the instructions tell the model to leave scope
    /// fields null, and a model that takes that to its conclusion replies `"scope": null`. That is a legal,
    /// maximally-reusable conclusion (see Validate), not an error — but deserialized onto a non-nullable
    /// property it would hand Validate a null and throw an NRE, which escapes ValidatedAgentRunner (it
    /// catches only JsonException) and fails the whole stage instead of costing one retry. An unscoped
    /// conclusion and an absent scope are the same thing; represent them the same way.
    public ConclusionScope Scope
    {
        get => _scope;
        set => _scope = value ?? Unscoped;
    }

    public string Finding { get; set; } = "";
    public double Confidence { get; set; }
}

/// Distils ONE applied revision into ONE reusable Learned Conclusion (design §6.1, Law 4).
///
/// WHY IT IS SHAPED THIS WAY — three constraints that look like ceremony and are not:
///
/// 1. It is a SEPARATE agent, not an extra field on DiscoveryOutput/RegulatoryOutput. An optional
///    "also emit a conclusion" field on a stage agent's schema is a field it can hallucinate on every
///    ORDINARY, non-revision run — and a fabricated conclusion does not stay in this project: it is
///    filed as cross-project knowledge that unrelated future projects will act on. The distiller only
///    runs when a revision was actually applied, so it cannot invent one out of nothing.
///
/// 2. The agent owns ONLY scope, finding and confidence. CODE owns id, kind, provenance and createdAt
///    (RevisionEffects.ConclusionKind picks the kind/partition key; the writer sets provenance). Provenance
///    is where the OPERATOR'S REASON IS PRESERVED VERBATIM. A distiller allowed to paraphrase
///    "overlaps the Ti Kβ line" into "improved tiering" would erase the only part of the record worth
///    keeping. The model generalizes the finding; it never gets to restate the evidence.
///
/// 3. Extracting the SCOPE ("this is about Ba in HDPE, not about this one bottle") is the judgement that
///    makes a conclusion reusable by a future, unrelated project — and it is also the highest-stakes place
///    a model could quietly rewrite history, which is why Validate refuses a scope.element the project
///    never contained.
///
/// A bag of raw operator sentences is not a knowledge layer. This agent is what turns one into one.
public static class ConclusionAgent
{
    public const string AgentName = "conclusion";

    public const string Instructions = """
        You are the SMX Conclusion agent. You receive an operator's revision (WHAT they changed and WHY),
        the project's components, and the stage output produced after the change was applied. Distil it
        into ONE reusable Learned Conclusion: a finding that a FUTURE, unrelated project should know.
        Rules:
        - The finding must be a generalized, self-contained sentence. A later reader has none of this
          project's context, so name the element / form / material it applies to inside the sentence
          itself. "Move it to tier C" is useless; "Barium is unsuitable for XRF-marked HDPE where Ti is
          present, because their K lines overlap" is a conclusion.
        - Ground it ONLY in the operator's reason and the stage output in front of you. Invent nothing —
          no CAS numbers, no regulations, no measurements that were not given to you.
        - Set a scope field ONLY where the revision genuinely constrains it; leave the rest null. An
          over-narrow scope hides the conclusion from the projects that need it; an over-broad one applies
          it where it does not hold. Only use an element that appears in this project.
        - confidence (0.0-1.0): one operator judgment on one project is evidence, not proof. Do not go
          above ~0.7 unless the reason cites a measurement or a regulation.
        Reply with ONLY a JSON object:
        { "scope": { "element", "form", "material", "application", "market", "substance" },
          "finding": "...", "confidence": 0.0 }
        """;

    public static Task<AgentRunResult<ConclusionOutput>> RunAsync(
        ISmxAgent agent, RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            revision = new { revision.Stage, revision.Target, revision.Reason },
            components = constraints.Components,
            stageOutputAfterTheChange = stageOutputJson,
        }, Json.Options);
        return ValidatedAgentRunner.RunAsync<ConclusionOutput>(agent,
            $"Distil this applied revision into one Learned Conclusion:\n{prompt}",
            o => Validate(o, constraints), ct);
    }

    internal static string? Validate(ConclusionOutput o, ConstraintsDoc constraints)
    {
        if (string.IsNullOrWhiteSpace(o.Finding))
            return "finding is required — a non-empty, generalized statement a future project could act on";
        if (o.Confidence is < 0 or > 1)
            return $"confidence must be between 0.0 and 1.0; got {o.Confidence}";
        if (!string.IsNullOrWhiteSpace(o.Scope.Element))
        {
            var pool = constraints.ElementPools.Select(p => p.Element).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!pool.Contains(o.Scope.Element))
                return $"scope.element '{o.Scope.Element}' is not an element in this project — a conclusion may " +
                       "only be scoped to an element it was actually drawn from";
        }
        return null;
    }
}
