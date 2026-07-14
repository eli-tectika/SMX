using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The project payload as CODE reads it. Deliberately NOT `IntakeOutput`, and deliberately not merged with
/// it: the two shapes overlap almost exactly, and conflating them is precisely how the MODEL's transcription
/// of the payload came to be the thing the ConstraintsDoc was built from.
///
/// Everything factual in the ConstraintsDoc is copied from HERE. See IntakeAgent.RunAsync.
internal sealed class IntakePayload
{
    public List<ComponentSpec> Components { get; set; } = [];
    public List<ElementPool> ElementPools { get; set; } = [];
    public List<CandidateSubstance> ProvidedCandidates { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    /// Absent until the physicist's XRF run lands (Law 6), so both stay at their empty defaults.
    ///
    /// SINGULAR, unlike ConstraintsDoc.MeasuredBackgrounds: this name is not a style choice, it is the
    /// PAYLOAD'S KEY (`measuredBackground`, the one ProjectEndpoints writes) which STJ binds by name. Rename
    /// it to match the doc and the binding misses — the physicist's background deserializes to an empty list
    /// and the ppm floor is computed without it, silently.
    public List<MeasuredBackground> MeasuredBackground { get; set; } = [];
    public XrfDevice? Device { get; set; }
}

/// The agent's reply. It KEEPS its echo fields even though nothing but `DerivedScope` is now read from it —
/// see IntakeAgent.Validate for why an echo that is never trusted is still worth demanding.
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
        provided candidates and the client restricted list must EXACTLY echo the input. The payload may also
        carry `measuredBackground` and `device` — the physicist's measured data. Do not echo them and do not
        reason about them; they are read straight from the payload by the system. Your added value is
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
        - Before proposing scope, call search_marker_library to find a prior approved code to reuse — pass the
          component's application, material and objective as SEPARATE arguments (never one combined phrase);
          if one fits, note it as a reuse candidate with its source project. Call
          search_learned_conclusions for prior findings on these materials/markets; treat any hit as prior
          evidence with confidence + provenance, never as ground truth, and never invent a conclusion if the
          tool returns no matches.
        Reply with ONLY a JSON object of shape:
        { "components": [...], "elementPools": [...], "providedCandidates": [...], "clientRestrictedList": [...],
          "derivedScope": [{ "listId", "componentId" ("*" for product-wide), "reason",
                             "citation": { "source", "reference", "retrievedAt" } }] }
        """;

    /// THE LAW OF THIS STAGE: code copies the FACTS out of the payload; the agent supplies only the JUDGMENT.
    ///
    /// Every factual field of the ConstraintsDoc below is read from `payload` — the operator's own input — and
    /// not one of them from `o`, the model's transcription of it. `DerivedScope` alone comes from the model,
    /// because deriving the regulatory scope is the only thing here the model is actually FOR.
    ///
    /// This is structural, not a guard on one field, and it deletes a class of bug rather than an instance.
    /// The payload now carries NUMBERS THAT BECOME MULTIPLIERS — `batchMassKg`, the measured background, the
    /// device LODs. A model that re-types 250 kg as 25 mis-doses a batch by 10x; a model that shaves a
    /// background level ships a marker under the detection floor that nobody can read in the field. Neither is
    /// a bug any downstream check catches, and neither would fail a test suite: Validate compares component
    /// IDS, not the values hanging off them, so both errors sail through as a successful intake run.
    ///
    /// The payload can also be deserialized here in the first place — the JsonException below is not caught.
    /// That is on purpose. StageDispatcher wraps the stage and reports a throw as `failed` WITH ITS MESSAGE,
    /// which names the payload. Parsing it inside Validate (as this used to) instead threw inside the runner's
    /// retry loop, which reports every JsonException as the AGENT's reply being malformed — so a poisoned
    /// payload cost three model round-trips and then blamed the model for it.
    public static async Task<AgentRunResult<ConstraintsDoc>> RunAsync(ISmxAgent agent, ProjectDoc project, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<IntakePayload>(project.Payload.GetRawText(), Json.Options)!;
        var prompt = $"Project constraints payload:\n{JsonSerializer.Serialize(project.Payload, Json.Options)}";
        var result = await ValidatedAgentRunner.RunAsync<IntakeOutput>(agent, prompt, o => Validate(o, payload), ct);
        if (!result.Succeeded) return AgentRunResult<ConstraintsDoc>.NeedsReview(result.Error!);
        return AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(project.ProjectId), ProjectId = project.ProjectId,
            Components = payload.Components,
            ElementPools = payload.ElementPools,
            ProvidedCandidates = payload.ProvidedCandidates,
            ClientRestrictedList = payload.ClientRestrictedList,
            MeasuredBackgrounds = payload.MeasuredBackground,
            Device = payload.Device,
            DerivedScope = result.Output!.DerivedScope,   // the model's, and only the model's
        });
    }

    /// The echo checks BELOW ARE KEPT even though nothing they check is read from the model any more.
    /// They are no longer a data guard — they are a COMPETENCE check. A model that cannot echo back the
    /// payload it was just handed has misread its input, and the derivedScope it reasoned from that misreading
    /// is worthless however well-formed it looks. That is worth catching and retrying on. The echo is simply
    /// no longer TRUSTED as data: it is evidence about the model, not a source of facts.
    ///
    /// (Note what this means for `clientRestrictedList`, which no check here has ever covered: a model could
    /// return `[]` and quietly empty the client's own banned-element list out of the product-wide gate. Taking
    /// it from the payload closes that hole without anyone having had to think of it — which is the point of
    /// fixing the mechanism instead of the symptom.)
    internal static string? Validate(IntakeOutput o, IntakePayload payload)
    {
        if (o.Components.Count != payload.Components.Count ||
            !o.Components.Select(c => c.Id).OrderBy(x => x).SequenceEqual(payload.Components.Select(c => c.Id).OrderBy(x => x)))
            return "components must exactly echo the input payload (no additions/removals)";
        static IEnumerable<string> Keys(IEnumerable<ElementPool> ps) =>
            ps.Select(p => $"{p.Component}|{p.Element}|{p.Line}").OrderBy(x => x);
        if (!Keys(o.ElementPools).SequenceEqual(Keys(payload.ElementPools)))
            return "element pools must exactly echo the input payload (no additions/removals)";
        static IEnumerable<string> CandidateKeys(IEnumerable<CandidateSubstance> cs) =>
            cs.Select(c => $"{c.ComponentId}|{c.Element}|{c.Cas}").OrderBy(x => x);
        if (!CandidateKeys(o.ProvidedCandidates).SequenceEqual(CandidateKeys(payload.ProvidedCandidates)))
            return "provided candidates must exactly echo the input payload (no additions/removals)";
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
