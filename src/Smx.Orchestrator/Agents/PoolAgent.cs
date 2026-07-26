using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class PoolAgentOutput
{
    public List<PoolSuggestion> Suggestions { get; set; } = [];
}

/// The need-driven pool proposer. It runs BEFORE Discovery, from the project's need alone, and proposes a
/// pool of candidate marker chemistries (element + form-class) per component. Unlike Discovery it MAY draw on
/// model knowledge and the open web and its citations are OPTIONAL: the pool is a HYPOTHESIS, and everything
/// downstream (the Background XRF filter, Discovery's catalog corroboration + tier rails, the regulatory gate)
/// is a sieve over it. It deliberately names ELEMENTS and FORM-CLASSES, never a CAS — the check-digit-guarded
/// CAS is Discovery's to mint, which keeps the highest-stakes error out of this stage structurally.
public static class PoolAgent
{
    public const string AgentName = "pool";

    /// The three canonical form-classes (the operator's taxonomy). A specific compound (oxide, carbonate, …)
    /// is a "compound" here; the specific choice lives in the rationale. A closed set keeps Validate honest.
    public static readonly string[] FormClasses = ["metal", "compound", "organocomplex"];

    public const string Instructions = """
        You are the SMX Pool agent. You receive a project's components — each with material, application, target
        markets, objective, and the substrate's physical state. Propose a POOL of candidate marker chemistries
        per component, from the need. This is a STARTING HYPOTHESIS: everything downstream (the XRF background
        filter, compatibility tiering, the regulatory gate) will sieve it, so breadth is welcome — but every
        suggestion must be chemically sensible.

        The marker will always be one of:
         - a metal element,
         - a metal compound (oxide, carbonate, sulfate, chloride, etc.), or
         - an organocomplex carrying the metal.
        The chosen form MUST match the substrate's physical state, e.g. oil / fuel-oil-soluble → organocomplex;
        a solid polymer → an oxide or salt; a coating → a dispersible compound.

        HOW TO WORK — do ALL of the following for EVERY component, and do not skip a step even when you feel
        confident. The value of this stage is your own knowledge COMBINED WITH retrieved evidence — never one
        alone:
         1. From your own general chemistry knowledge, draft candidate markers whose form-class matches the
            substrate's physical state.
         2. You MUST then SEARCH to corroborate and widen that draft — always call, at minimum:
              - search_reference — the SMX corpus (solubility, XRF cleanliness, form/physical-state fit), and
              - the web search tool — for broader or more recent candidate chemistries and additives.
            Also call search_learned_conclusions and search_marker_library when the material/application hints
            at prior evidence or a reusable approved code. Web queries must contain ONLY chemistry — never a
            client, product or project name.
         3. MERGE the two sources into the final pool: keep the markers your knowledge proposed AND any the
            searches surfaced; drop nothing solely because one source omitted it. Deduplicate by
            element + form-class.

        For each marker give:
         - element: the metal element symbol,
         - formClass: EXACTLY one of "metal" | "compound" | "organocomplex" (put the specific compound, e.g.
           "oxide", in the rationale),
         - rationale: one sentence that says why this element+form suits the substrate's physical state AND
           names its basis — general chemistry knowledge, a reference hit, and/or a web source.

        Do NOT state a CAS number — the exact form and its CAS are chosen later by Discovery from the catalog.
        Only propose markers for declared components.

        Reply with ONLY a JSON object:
        { "suggestions": [{ "component", "element", "formClass" ("metal"|"compound"|"organocomplex"),
          "rationale", "citations": [{ "source", "reference", "retrievedAt" }] }] }
        CITE every suggestion a retrieved result supports (source, reference, retrievedAt = now, ISO 8601 UTC).
        A suggestion resting only on your own knowledge may omit citations, but its rationale must say so.
        """;

    /// <param name="revision">null for an ordinary run; non-null re-runs applying the operator's
    /// revise-with-reason (Law 4). Explicit, not an overload, so a caller who forgets it gets a compile error.</param>
    public static async Task<AgentRunResult<PoolDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new { components = constraints.Components }, Json.Options);
        var task = revision is null
            ? $"Propose a candidate marker pool for these components:\n{prompt}"
            : RevisionTask(revision, prompt);

        // The SIMPLE overload, deliberately — NOT the web-aware one Discovery uses. Web-citation stamping
        // exists to power Discovery's "web-only ⇒ Tier B, never preferred" rail; the pool has no tier and no
        // such rail, so there is nothing here for stamped provenance to gate. A pool suggestion is a
        // hypothesis Discovery re-derives and cites from scratch.
        var result = await ValidatedAgentRunner.RunAsync<PoolAgentOutput>(
            agent, task, o => Validate(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<PoolDoc>.NeedsReview(result.Error!);
        return AgentRunResult<PoolDoc>.Ok(new PoolDoc
        {
            Id = RecordIds.Pool(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Suggestions = result.Output!.Suggestions, Source = "agent",
        });
    }

    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-propose the candidate marker pool for these components, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. Where it cannot be supported by chemistry,
        apply it anyway and say exactly that in the affected suggestion's rationale.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    internal static string? Validate(PoolAgentOutput o, ConstraintsDoc constraints)
    {
        if (o.Suggestions.Count == 0) return "at least one marker suggestion is required";
        var componentIds = constraints.Components.Select(c => c.Id).ToHashSet();
        foreach (var s in o.Suggestions)
        {
            if (!componentIds.Contains(s.Component))
                return $"suggestion references unknown component '{s.Component}'";
            if (string.IsNullOrWhiteSpace(s.Element))
                return "every suggestion must name an element";
            if (!FormClasses.Contains(s.FormClass))
                return $"suggestion '{s.Element}' has formClass '{s.FormClass}'; it must be one of " +
                       $"{string.Join(" | ", FormClasses)}";
            if (string.IsNullOrWhiteSpace(s.Rationale))
                return $"suggestion '{s.Element}/{s.FormClass}' is missing its rationale — every suggestion " +
                       "must name why it suits the substrate and what its basis is";
        }
        return null;
    }
}
