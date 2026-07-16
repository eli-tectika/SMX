using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Agents;

public sealed class ToolBox(
    ICatalogLookup catalog,
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference,
    IKnowledgeStore knowledge,
    ILearnedConclusionsSearch learnedConclusions,
    Func<SensitiveTerms, IWebSearch> webSearchFactory,
    bool useHostedWebSearch)
{
    /// SensitiveTerms is a REQUIRED parameter, not an optional one: the PROXY tool set built without the
    /// project's client/product names is a tool set that cannot protect the project. Forgetting it is a
    /// compile error — the same reasoning the codebase applies to RevisionDoc? in the agent runners. (The
    /// hosted tool cannot pre-strip terms; there the same rule is carried by the Discovery instructions and
    /// re-enforced deterministically downstream — see HostedWebSearch.)
    public IList<AITool> DiscoveryTools(SensitiveTerms terms) =>
    [
        .. DiscoveryReadTools(),
        useHostedWebSearch ? HostedWebSearch() : ProxyWebSearch(terms),
    ];

    /// The model's built-in, provider-executed web search (Responses API, WEB_SEARCH_PROVIDER=hosted). Unlike
    /// the proxy tool it composes and runs its OWN query server-side, so it takes no SensitiveTerms and cannot
    /// pre-strip them: the "query must contain NO client/product/project name" rule lives in the Discovery
    /// instructions instead. RAIL 1 (web-only ⇒ ≤ Tier B, never preferred) is NOT lost — it is re-established
    /// in code downstream: MafAgent captures the URLs the tool actually returned (its CitationAnnotations) and
    /// DiscoveryAgent.Validate re-stamps any candidate citation matching one as web-derived, so the tier rail
    /// still rests on a code-observed fact, not on the model's self-reported citation source.
    private static AITool HostedWebSearch() => new HostedWebSearchTool();

    /// The legacy anonymizing egress (WEB_SEARCH_PROVIDER=proxy). Kept for revival; closes over the project's
    /// SensitiveTerms and stamps "web:<host>" on every hit itself, which is what makes RAIL 1 deterministic on
    /// this path without any downstream re-stamping.
    private AITool ProxyWebSearch(SensitiveTerms terms)
    {
        var web = webSearchFactory(terms);
        return AIFunctionFactory.Create(
            (string query, string intent, CancellationToken ct) => SearchWebAsync(query, intent, ct, web),
            "search_web",
            "Anonymized external web search, for candidate forms the SMX catalog does not carry. It is a STARTING POINT, not an authority: a web hit may suggest a marker, it can never endorse one. " +
            "Corroborate every web finding against search_catalog and search_reference before you rely on it, and NEVER state a CAS you did not read from a retrieved source. " +
            "A candidate supported only by web citations must be Tier B with its limitation named in the rationale — it can never be Tier A or preferred. " +
            "The query must contain NO client, product or project name — only chemistry. " +
            "intent must be one of: discovery.candidate_forms, discovery.form_properties, discovery.supplier_availability.");
    }

    /// The Discovery retrieval tools MINUS search_web — the read surface with no egress. A CHAT turn for
    /// the Discovery stage gets exactly this (ReadToolsFor): the operator's conversational surface can
    /// look a candidate up in the catalog and the corpus, but it is deliberately NOT a second web-egress
    /// trigger. Egress stays confined to the autonomous Discovery run — the single anonymized channel the
    /// Search Proxy exists to control. A web search a chat turn wants, the operator asks the run to make.
    public IList<AITool> DiscoveryReadTools() =>
    [
        AIFunctionFactory.Create(SearchCatalogAsync, "search_catalog",
            "List the catalog products (form, molecule, CAS, purity, supplier) available for an element from the SMX catalog. Call this FIRST — it is the authoritative source for a candidate's CAS."),
        AIFunctionFactory.Create(LookupCompatibilityAsync, "lookup_compatibility",
            "Exact tabulated element×substrate compatibility verdict. Use as a tiering signal — an incompatible substrate lowers a candidate's tier or excludes it."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose: solubility, XRF cleanliness, marker forms, bibliography-backed notes. Use to justify form ranking and tiering."),
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Search accumulated Learned Conclusions (prior material/regulatory findings with confidence + provenance) relevant to tiering this element/form. Treat them as prior evidence, not fact; a higher-confidence, more recent conclusion supersedes an older one."),
    ];

    public IList<AITool> RegulatoryTools() =>
    [
        AIFunctionFactory.Create(SearchRegulatoryAsync, "search_regulatory",
            "Search the official regulatory corpus (REACH/RoHS/PPWR/SVHC/Prop 65/EU Cosmetics/FDA...). Call this for the ElementGate and ApplicationCheck dimensions. Cite every returned reference you rely on."),
        AIFunctionFactory.Create(SearchSdsAsync, "search_sds",
            "Search safety-data-sheet (GHS) chunks by CAS or element: H-codes, CMR, hazard classifications. Call this for the Hazard dimension."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose: solubility, XRF cleanliness, marker forms, bibliography-backed notes."),
    ];

    public IList<AITool> IntakeTools() =>
    [
        AIFunctionFactory.Create(SearchRegulatoryAsync, "search_regulatory",
            "Search the official regulatory corpus to confirm which regulation lists apply to a component given its application and target markets. Cite every list you include."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose for material/application background."),
        AIFunctionFactory.Create(SearchMarkerLibraryAsync, "search_marker_library",
            "Search the cross-project Marker Library for a previously approved code to reuse. Pass the component's application, material, and/or objective as SEPARATE arguments (each is matched independently — do not pass one combined phrase). Omit a dimension to leave it unconstrained. Prefer reusing a validated code over inventing a new one; cite the source project."),
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Search accumulated Learned Conclusions (prior material/regulatory findings with confidence + provenance) relevant to this intake. Treat them as prior evidence, not fact; a higher-confidence, more recent conclusion supersedes an older one."),
    ];

    /// The Dosing retrieval tools MINUS the two calculators — the read surface with no computation. A CHAT
    /// turn for the Dosing stage gets exactly this (ReadToolsFor): it answers from the already-computed
    /// DosingDoc and these sources, it does NOT recompute a floor or an order amount. Mirrors
    /// DiscoveryReadTools (the read half) vs DiscoveryTools (the autonomous run's full set).
    public IList<AITool> DosingReadTools() =>
    [
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Prior ppm and dosing findings from earlier projects, with the reasons they were recorded."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "The reference corpus — formulation-impact basis, application notes, typical loadings."),
    ];

    /// Decision reads what Dosing reads: prior conclusions and the reference corpus. The decision matrix
    /// itself is DETERMINISTIC assembly — there is deliberately no tool that could let the model "look up"
    /// a different answer than the record it is proposing over.
    public IList<AITool> DecisionReadTools() => DosingReadTools();

    /// The Dosing stage's tools. The two calculators are DETERMINISTIC and the agent may not do their
    /// arithmetic itself: a model that computes its own floor can compute one that is too low, and a low
    /// floor ships a marker nobody can detect. It calls these, and picks a ppm INSIDE what they return.
    ///
    /// Closed over the project's ConstraintsDoc for the same reason DiscoveryTools closes over SensitiveTerms:
    /// the floor is targeted at THIS project's measured background and device, and no method here may compute
    /// against another project's physics. The DosingAgent (Task 11) is the consumer; the CHAT surface gets
    /// DosingReadTools() only — it answers from the already-computed DosingDoc, it does not recompute a floor.
    public IList<AITool> DosingTools(ConstraintsDoc constraints) =>
    [
        AIFunctionFactory.Create(
            (string componentId, string element, CancellationToken ct) => DetectionFloorJson(constraints, componentId, element),
            "detection_floor",
            "The ppm detection and quantification floors for an element in a component, computed from the " +
            "physicist's MEASURED background and the deployment device's LOD. If the measurement is missing " +
            "this returns an error and NO number — say so; never estimate a floor."),
        AIFunctionFactory.Create(
            (string cas, double ppm, string componentId, CancellationToken ct) => OrderAmountAsync(constraints, cas, ppm, componentId, ct),
            "order_amount",
            "How many grams of the COMPOUND to order for a given ppm in a component, from the batch mass and " +
            "the substance's metal loading. If the loading is unknown this returns an error naming the CAS — " +
            "the operator must supply it; never assume one."),
        .. DosingReadTools(),
    ];

    /// The READ tools a CHAT turn gets for a stage (ChatAgent, design §5) — deliberately the same retrieval
    /// surface the stage's own agent reasoned with, so chat can answer for what the stage produced FROM ITS
    /// SOURCES rather than from the model's memory. A switch over the existing sets, never a new set: chat
    /// must not be able to retrieve what the stage itself could not, nor to miss what it could.
    ///
    /// Note what no branch can return: nothing here writes, approves or signs. The mutating half of a chat
    /// turn comes from ChatTools, which is bound to one project and offers no gate tool at all — so chat
    /// cannot sign a gate because the capability does not exist, not because it was told not to (Law 9).
    ///
    /// Matrix — and any stage we do not recognise — gets NOTHING, which is fail-closed. Matrix derives its
    /// output from the record it is handed, so there is no corpus to search; an unknown stage is a bug
    /// upstream, and the safe response to a bug is no capability. A tool-less chat agent can still answer
    /// from the stage inputs in its prompt, or say it has no source — which is the answer we want anyway.
    public IList<AITool> ReadToolsFor(string stage) => stage switch
    {
        Stages.Intake => IntakeTools(),
        Stages.Discovery => DiscoveryReadTools(),
        Stages.Regulatory => RegulatoryTools(),
        Stages.Dosing => DosingReadTools(),
        Stages.Decision => DecisionReadTools(),
        // Cost is deterministic — its output is a table lookup, not a reasoned claim over a corpus — so a
        // chat turn on it holds NO read tools and answers only from the CostDoc in its prompt. Listed
        // explicitly (not left to the default) so the intent reads as deliberate, not as an unknown stage.
        Stages.Cost => [],
        _ => [],
    };

    public async Task<string> SearchCatalogAsync(string element, CancellationToken ct)
    {
        var cards = await catalog.LookupAsync(element, ct);
        return cards.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent CAS numbers; exclude this element or mark the candidate lower-confidence\"}"
            : JsonSerializer.Serialize(new { results = cards }, Json.Options);
    }

    public async Task<string> LookupCompatibilityAsync(string element, string substrate, CancellationToken ct)
    {
        var card = await compatibility.LookupAsync(element, substrate, ct);
        return card is null
            ? $"{{\"tabulated\":false,\"note\":\"{element}×{substrate} not tabulated — treat as a weak signal\"}}"
            : JsonSerializer.Serialize(new { tabulated = true, card }, Json.Options);
    }

    /// The defaults are not decoration: AIFunctionFactory emits a parameter without one as REQUIRED in the
    /// tool's JSON schema. Without them the binding rejects the very call the description invites ("omit a
    /// dimension to leave it unconstrained") — e.g. an intake with an application + material but no objective.
    public async Task<string> SearchMarkerLibraryAsync(
        string? application = null, string? material = null, string? objective = null, CancellationToken ct = default)
    {
        var markers = await knowledge.FindMarkersAsync(application, material, objective, ct);
        return markers.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior approved code fits; proceed without reuse, do not invent one\"}"
            : JsonSerializer.Serialize(new { results = markers }, Json.Options);
    }

    public async Task<string> SearchLearnedConclusionsAsync(string query, CancellationToken ct)
    {
        var chunks = await learnedConclusions.SearchAsync(query, 5, ct);
        return chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior conclusions on this; reason from primary sources, do not fabricate a prior finding\"}"
            : Render(chunks);
    }

    /// The ppm detection/quantification floor for one element in one component. PURE over the project's
    /// constraints — the physicist's measured background + the deployment device's LOD — so it takes no
    /// CancellationToken body and never touches the knowledge store: there is nothing to fetch, and the
    /// arithmetic must be the one thing the agent cannot influence. On any missing/ambiguous input
    /// DetectionFloor.Compute returns an error and NO Floor, and this emits the error VERBATIM with no
    /// number in any field — a floor the agent could talk down is a marker nobody can detect.
    private string DetectionFloorJson(ConstraintsDoc constraints, string componentId, string element)
    {
        var (floor, error) = DetectionFloor.Compute(constraints.MeasuredBackgrounds, constraints.Device, componentId, element);
        return floor is not null
            ? JsonSerializer.Serialize(new { floor }, Json.Options)
            : JsonSerializer.Serialize(new { error }, Json.Options);
    }

    /// Grams of the COMPOUND to order for a ppm in a component. The metal loading lives in NO catalog we have;
    /// it is read by CAS from the cross-project knowledge layer, and a cold miss is a REFUSAL that names the
    /// CAS and tells the operator to supply it — never a default to 1.0 ("it's the pure metal"), which is the
    /// exact assumption that under-orders an oxide below the detection floor. The batch mass comes from the
    /// component; an absent one is surfaced by OrderAmount.Compute's own refusal, not crashed on.
    private async Task<string> OrderAmountAsync(
        ConstraintsDoc constraints, string cas, double ppm, string componentId, CancellationToken ct)
    {
        var component = constraints.Components.FirstOrDefault(c => c.Id == componentId);

        var property = await knowledge.GetSubstancePropertyAsync(cas, ct);
        if (property is null)
            return JsonSerializer.Serialize(new
            {
                error = $"no metal loading is recorded for CAS {cas}, so the order amount cannot be computed. " +
                        "The operator must enter the substance's metal loading (with its basis) — it lives in no " +
                        "catalog, and assuming the pure metal (1.0) under-orders an oxide below the detection floor.",
            }, Json.Options);

        var (order, error) = OrderAmount.Compute(ppm, component?.BatchMassKg, property.MetalLoading);
        return order is not null
            ? JsonSerializer.Serialize(new { order }, Json.Options)
            : JsonSerializer.Serialize(new { error }, Json.Options);
    }

    /// A web hit is NOT a RetrievedChunk. Its source is "web:<host>" so that a citation built from it stays
    /// machine-identifiable as web-derived all the way into the candidates doc — which is what lets
    /// DiscoveryAgent.Validate enforce the Tier-A rail deterministically instead of trusting the prompt.
    ///
    /// This overload exists so a test can drive the method directly, the way ToolBoxTests drives
    /// SearchCatalogAsync. The tool the model actually calls closes over the PER-PROJECT IWebSearch that
    /// DiscoveryTools built from the project's SensitiveTerms — never this one.
    ///
    /// It takes the terms EXPLICITLY for the same reason DiscoveryTools does: no method on this class may
    /// build a web search that was never told what it must not send. A convenience overload defaulting to an
    /// empty term list would be an unguarded egress path sitting on a public API, one careless
    /// AIFunctionFactory.Create away from being handed to the model.
    public async Task<string> SearchWebAsync(string query, string intent, SensitiveTerms terms, CancellationToken ct) =>
        await SearchWebAsync(query, intent, ct, webSearchFactory(terms));

    private static async Task<string> SearchWebAsync(string query, string intent, CancellationToken ct, IWebSearch web)
    {
        var result = await web.SearchAsync(query, intent, ct);

        // A refusal/failure is NOT "no matches". Relay the note so the agent can tell "I searched and found
        // nothing" from "I never got an answer" — treating the second as the first is how a good marker gets
        // confidently excluded.
        if (result.Note is not null)
            return JsonSerializer.Serialize(new { results = Array.Empty<object>(), note = result.Note }, Json.Options);

        if (result.Hits.Count == 0)
            return "{\"results\":[],\"note\":\"no matches — do not invent facts; stay with the catalog candidates\"}";

        return JsonSerializer.Serialize(new
        {
            results = result.Hits.Select(h => new AgentVisibleChunk($"web:{h.Host}", h.Url, $"{h.Title} — {h.Snippet}")),
            note = "WEB SOURCE: a starting point, not an authority. Corroborate against search_catalog before relying on it. " +
                   "A candidate whose citations are all web sources must be Tier B, never Tier A and never preferred.",
        }, Json.Options);
    }

    public async Task<string> SearchRegulatoryAsync(string query, CancellationToken ct) => Render(await regulatory.SearchAsync(query, ct: ct));
    public async Task<string> SearchSdsAsync(string query, CancellationToken ct) => Render(await sds.SearchAsync(query, ct: ct));
    public async Task<string> SearchReferenceAsync(string query, CancellationToken ct) => Render(await reference.SearchAsync(query, ct: ct));

    /// What an agent is shown for one hit — RetrievedChunk MINUS its Score. Deliberate: search_learned_conclusions
    /// is a HYBRID query, so its score is RRF (~0.01–0.03), while search_regulatory / search_sds / search_reference
    /// are BM25 (~1–10). Both land in the same context window on incomparable scales, and an LLM has no way to know
    /// that — it reads 0.016 as "weak evidence" and quietly discounts the very prior conclusion the knowledge loop
    /// exists to surface, in favour of a raw corpus hit. Nothing in C# reads Score, and an agent does not need it:
    /// it cites by Reference, and a Learned Conclusion carries its own calibrated `confidence: 0.70` INSIDE its
    /// content (LearnedConclusionProjection.Content) — which is what the Intake/Discovery instructions tell the
    /// model to weigh. Score stays on RetrievedChunk for logging/eval; it just never reaches the model.
    private sealed record AgentVisibleChunk(string Source, string Reference, string Content);

    private static string Render(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent facts; lower confidence or mark NeedsReview\"}"
            : JsonSerializer.Serialize(
                new { results = chunks.Select(c => new AgentVisibleChunk(c.Source, c.Reference, c.Content)) }, Json.Options);
}
