using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Agents;

public sealed class DiscoveryOutput
{
    public List<CandidateSubstance> Substances { get; set; } = [];
}

/// The pool pass's output. TWO lists, in this order, because that is the order the work must happen in: the
/// model emits every element choice BEFORE it emits a single form, so the forms are generated conditioned on
/// the element list rather than the element list being written afterwards to justify the forms.
public sealed class PoolOutput
{
    public List<PoolElementChoice> Elements { get; set; } = [];
    public List<PoolSuggestion> Suggestions { get; set; } = [];
}

/// ONE agent for one operator move. Discovery runs in TWO PASSES over the same question — propose a pool of
/// marker chemistries, then corroborate each one into a fully-specified substance — and until 2026-08-06 the
/// first pass was a separate `PoolAgent` with its own name, prompt and identity. That split was a seam the
/// operator had to understand for no benefit: two agents implied two opinions, and the pool is not a second
/// opinion about discovery, it is the first half of it.
///
/// What the merge does NOT do is collapse the two PASSES into one model call. They ask different questions of
/// different tools (the pool pass may reach past the catalog and treats the web as a first-class source; the
/// corroboration pass must mint a CAS and is hard-railed to the catalog), and each has validation of its own.
/// One agent, one name in the run trail, two passes.
///
/// The `pool` STAGE also survives, deliberately. It is what makes the pool a re-runnable, individually
/// resettable unit of work with its own artifact (PoolDoc — the thing the Discovery screen shows as "what is
/// being corroborated") and its own skip conditions (an operator-supplied element pool, provided candidates).
/// Deleting the agent and deleting the stage are different decisions; only the first was the seam.
public static class DiscoveryAgent
{
    public const string AgentName = "discovery";

    /// The three canonical form-classes (the operator's taxonomy). A specific compound (oxide, carbonate, …)
    /// is a "compound" here; the specific choice lives in the rationale. A closed set keeps ValidatePool honest.
    public static readonly string[] FormClasses = ["metal", "compound", "organocomplex"];

    /// The pool's target breadth, PER COMPONENT. It exists because the prompt named no number at all, and real
    /// runs came back with three candidates for one component and two for another — a pool that thin decides
    /// the project before any evidence is gathered, since nothing downstream can recover an element that was
    /// never proposed. It is a TARGET and not a cap, and deliberately NOT enforced in ValidatePool: a hard
    /// minimum would be answered by padding the list, and an invented element is worse than a short one. The
    /// only count rail in code is coverage — every component gets something (see ValidatePool).
    ///
    /// One number, one place: it is interpolated into both the instructions and the per-run task, which is why
    /// PoolInstructions is `static readonly` rather than `const` like the other agents'.
    public const int TargetElementsPerComponent = 10;

    /// PASS 1 — the need-driven pool. May draw on model knowledge and the open web, and its citations are
    /// OPTIONAL: the pool is a HYPOTHESIS and everything downstream (pass 2's catalog corroboration and tier
    /// rails, the regulatory screen) is a sieve over it. It names ELEMENTS and FORM-CLASSES, never a CAS —
    /// keeping the highest-stakes error (a wrong CAS) structurally out of a pass that is allowed to speculate.
    public static readonly string PoolInstructions = $$"""
        You are the SMX Discovery agent, running your FIRST PASS: proposing the candidate marker POOL. You
        receive a project's components — each with material, application, target markets, objective, and the
        substrate's physical state. This is a STARTING HYPOTHESIS: your own second pass corroborates every
        suggestion against the catalog and the regulatory screen sieves what survives, so BREADTH IS WELCOME —
        but every suggestion must be chemically sensible.

        WORK IN TWO STEPS, FOR EVERY COMPONENT, IN THIS ORDER. They are different questions asked against
        different evidence — what can be READ here, versus what FORM survives this material — and answering
        them together loses the second.

        STEP 1 — ELEMENTS. For each component, choose the elements worth marking with at all. An element
        qualifies when it is:
          - detectable by XRF on this substrate,
          - clean against the background this material and its process are expected to carry,
          - plausible for this application and its target markets.
        Aim for about {{TargetElementsPerComponent}} elements FOR EACH COMPONENT. That is a TARGET, not a cap:
        propose more where the chemistry supports it, and fewer ONLY where the substrate genuinely constrains
        the choice — and when you do come in under the target, name the constraint in that component's element
        rationales. Count PER COMPONENT, never across the project: a component given two elements has had its
        analysis decided before any evidence was gathered, however many the other components got.
        Do NOT consider molecular form yet.

        STEP 2 — MOLECULES. Only now, and for EACH element you chose, break it into candidate molecular forms
        and keep those that suit that component's physical state:
          - oil / fuel-oil-soluble  -> an organocomplex carrying the metal
          - solid polymer           -> an oxide or a salt
          - coating                 -> a dispersible compound
        The marker will always be a metal element, a metal compound (oxide, carbonate, sulfate, chloride, …),
        or an organocomplex. One element may yield several forms; each form is its own suggestion.

        SEARCHING — do this in BOTH steps, and do not skip it even when you feel confident. The value of this
        pass is your own knowledge COMBINED WITH retrieved evidence, never one alone. Always call, at minimum:
          - search_reference — the SMX corpus (solubility, XRF cleanliness, form/physical-state fit), and
          - the web search tool — for broader or more recent candidate chemistries and additives.
        Also call search_learned_conclusions and search_marker_library when the material/application hints at
        prior evidence or a reusable approved code. Web queries must contain ONLY chemistry — never a client,
        product or project name. MERGE the two sources: keep what your knowledge proposed AND what the searches
        surfaced; drop nothing solely because one source omitted it. Deduplicate by element + form-class.

        Reply with ONLY a JSON object, "elements" first:
        { "elements": [{ "component", "element", "rationale",
                         "citations": [{ "source", "reference", "retrievedAt" }] }],
          "suggestions": [{ "component", "element",
                            "formClass" ("metal"|"compound"|"organocomplex"),
                            "rationale", "citations": [{ "source", "reference", "retrievedAt" }] }] }
        Every element's rationale says why it is detectable and clean HERE; every suggestion's rationale says
        why that form suits the substrate's physical state. Both name their basis — general chemistry
        knowledge, a reference hit, and/or a web source.
        Every suggestion's element MUST appear in "elements" for that same component, and every element you
        list MUST yield at least one suggestion.
        Propose markers only for the components you are given, and give EVERY one of them a pool.
        Do NOT state a CAS number — the exact form and its CAS are yours to mint in the second pass, from the
        catalog.
        CITE every element and every suggestion a retrieved result supports (source, reference,
        retrievedAt = now, ISO 8601 UTC). One resting only on your own knowledge may omit citations, but its
        rationale must say so.
        """;

    /// PASS 2 — corroboration. Unchanged by the merge: it answers only from tools, mints the CAS, and carries
    /// the deterministic rails (see Validate).
    public const string Instructions = """
        You are the SMX Discovery agent, running your SECOND PASS: corroborating the pool you proposed into
        fully-specified substances. For each component you receive its usable/conditional element POOL
        (V = clean, L = conditional) plus material, application and objective. Turn each pooled element into
        one or more FULLY-SPECIFIED candidate substances: element + molecular form + CAS + (particle size,
        solvent when known). You may only use facts from your tools:
        - search_catalog(element) FIRST — the SMX catalog is the authoritative source for a CAS.
        - the web search tool ONLY when the catalog does not carry a form you have good reason to believe
          exists. It is a starting point, not an authority: its results may suggest a candidate; they may
          never endorse one. Corroborate anything you find against search_catalog / search_reference, and put
          the page URL in that citation's `reference`. The query must contain NO client, product or project
          name — only chemistry.
        - search_reference for solubility / XRF cleanliness / form ranking evidence.
        - lookup_compatibility(element, substrate) as a tiering signal (incompatible ⇒ lower tier or C).
        - search_learned_conclusions when tiering an element/form. A higher-confidence, more recent
          conclusion supersedes an older one. If the tool returns no matches, tier from the primary sources —
          do not fabricate a prior finding.
        NEVER state a CAS you did not read from a retrieved source; a CAS is check-digit validated and a
        wrong one will be rejected. A candidate whose citations are ALL web sources must be Tier B, must not
        be preferred, and must name that limitation in its rationale.
        If a tool tells you the external search failed or was refused, that is NOT evidence that no such
        marker exists — say so, and continue from the catalog.
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

    /// PASS 1 — propose the pool. Runs on the `pool` stage, with the pool tool surface (ToolBox.PoolTools):
    /// no search_catalog, because reaching past the catalog is the point, and a web tool the pass may use
    /// freely because nothing it writes is an endorsement.
    ///
    /// The SIMPLE ValidatedAgentRunner overload, deliberately — NOT the web-aware one the corroboration pass
    /// uses. Citation stamping exists to power that pass's "web-only ⇒ Tier B, never preferred" rail; the pool
    /// carries no tier and no such rail, so there is nothing here for stamped provenance to gate. A pool
    /// suggestion is a hypothesis the second pass re-derives and cites from scratch.
    ///
    /// <param name="revision">null for an ordinary run; non-null re-proposes applying the operator's
    /// revise-with-reason (Law 4). Explicit, not an overload, so a caller who forgets it gets a compile
    /// error.</param>
    public static async Task<AgentRunResult<PoolDoc>> RunPoolAsync(
        ISmxAgent agent, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new { components = constraints.Components }, Json.Options);
        // The target is restated in the TASK, not left to the instructions alone, and it names the component
        // COUNT: "about ten elements" read against a list of four components is the sentence that produced a
        // ten-element project instead of a ten-element component.
        var task = revision is null
            ? $"Propose a candidate marker pool for these {constraints.Components.Count} components. " +
              $"Target about {TargetElementsPerComponent} elements for EACH component separately — " +
              $"per component, not across the project:\n{prompt}"
            : PoolRevisionTask(revision, prompt);

        var result = await ValidatedAgentRunner.RunAsync<PoolOutput>(
            agent, task, o => ValidatePool(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<PoolDoc>.NeedsReview(result.Error!);
        return AgentRunResult<PoolDoc>.Ok(new PoolDoc
        {
            Id = RecordIds.Pool(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Elements = result.Output!.Elements, Suggestions = result.Output!.Suggestions, Source = "agent",
        });
    }

    private static string PoolRevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-propose the candidate marker pool for these components, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. Where it cannot be supported by chemistry,
        apply it anyway and say exactly that in the affected suggestion's rationale.
        Work the same two steps — elements for every component first, then the forms for each element.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    /// The pool pass's rails. All of them are SHAPE, not judgement: nothing here can tell a good element from
    /// a bad one, and pretending otherwise would only teach the model to satisfy the check.
    internal static string? ValidatePool(PoolOutput o, ConstraintsDoc constraints)
    {
        if (o.Elements.Count == 0)
            return "the \"elements\" step is missing — choose the elements for each component FIRST, then " +
                   "break each chosen element into forms";
        if (o.Suggestions.Count == 0) return "at least one marker suggestion is required";

        var componentIds = constraints.Components.Select(c => c.Id).ToHashSet();
        var chosen = new HashSet<(string Component, string Element)>();
        foreach (var e in o.Elements)
        {
            if (!componentIds.Contains(e.Component))
                return $"element choice references unknown component '{e.Component}'";
            if (string.IsNullOrWhiteSpace(e.Element))
                return "every element choice must name an element";
            if (string.IsNullOrWhiteSpace(e.Rationale))
                return $"element '{e.Element}' for component '{e.Component}' is missing its rationale — " +
                       "every chosen element must say why it is detectable and clean on this substrate";
            chosen.Add((e.Component, e.Element));
        }

        // RAIL — THE TWO STEPS MUST BOTH HAVE HAPPENED. A form whose element was never chosen means step 2
        // ran without step 1 for that element; an element that yielded no form means step 1's answer was
        // dropped on the way to step 2. Either way the collapse this pass exists to prevent has happened, and
        // it is invisible in the output unless it is checked here.
        var formsFor = new HashSet<(string Component, string Element)>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var s in o.Suggestions)
        {
            if (!componentIds.Contains(s.Component))
                return $"suggestion references unknown component '{s.Component}'";
            if (string.IsNullOrWhiteSpace(s.Element))
                return "every suggestion must name an element";
            if (!chosen.Contains((s.Component, s.Element)))
                return $"suggestion '{s.Element}' for component '{s.Component}' names an element that is not " +
                       "in your \"elements\" list for that component — choose the element first, with its own " +
                       "rationale, then break it into forms";
            if (!FormClasses.Contains(s.FormClass))
                return $"suggestion '{s.Element}' has formClass '{s.FormClass}'; it must be one of " +
                       $"{string.Join(" | ", FormClasses)}";
            if (string.IsNullOrWhiteSpace(s.Rationale))
                return $"suggestion '{s.Element}/{s.FormClass}' is missing its rationale — every suggestion " +
                       "must name why it suits the substrate and what its basis is";
            // Deduplicate, in code as well as in the prompt: a repeated (component, element, form-class) is
            // breadth on paper only, and the one number this pass is asked to hit is a count.
            if (!seen.Add((s.Component, s.Element, s.FormClass)))
                return $"suggestion '{s.Element}/{s.FormClass}' is listed twice for component " +
                       $"'{s.Component}' — deduplicate by element + form-class";
            formsFor.Add((s.Component, s.Element));
        }

        var unbroken = chosen.Where(c => !formsFor.Contains(c)).ToList();
        if (unbroken.Count > 0)
            return $"{string.Join(", ", unbroken.Select(c => $"'{c.Element}' in '{c.Component}'"))} " +
                   "— chosen as an element but broken into no form. Give each chosen element at least one " +
                   "form that suits the substrate, or drop the element and say nothing about it";

        // RAIL — EVERY COMPONENT GETS A POOL. Per-component tracks are the product's core rule: background,
        // form, ppm and codes all run independently per component, so a component with no proposed markers is
        // a component that quietly leaves the analysis. Nothing downstream can recover it — Discovery only
        // corroborates what the pool proposed — and the record would report a finished project with a silent
        // hole in it. Coverage is the ONLY count checked; the ~10 target is judgement and stays in the prompt.
        var uncovered = constraints.Components
            .Select(c => c.Id)
            .Where(id => !o.Suggestions.Any(s => s.Component == id))
            .ToList();
        if (uncovered.Count > 0)
            return $"component(s) {string.Join(", ", uncovered.Select(id => $"'{id}'"))} have no proposed " +
                   "markers. Every declared component needs its own pool — they are marked independently, " +
                   "and a component nothing is proposed for is dropped from the analysis entirely";

        return null;
    }

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
            (o, webCitations) => { StampWebCitations(o, webCitations); return Validate(o, constraints); }, ct);
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

    /// Deterministic RAIL-1 re-hardening for the hosted web-search path. The hosted tool composes and runs its
    /// OWN query server-side, so — unlike the proxy tool — nothing in our code stamps "web:" on the citations
    /// the model then writes. We do it here from a fact the model cannot forge: the set of URLs the tool
    /// actually returned this turn (captured by MafAgent from the response's CitationAnnotations). Any citation
    /// pointing at one of those hosts is rewritten to source "web:&lt;host&gt;", so Validate's existing
    /// web-only Tier rail fires on it exactly as on the proxy path, and the persisted CandidatesDoc records
    /// the web provenance either way.
    ///
    /// Biased toward stamping: a wrongly-stamped catalog citation only makes the rail STRICTER (the agent is
    /// asked to re-cite and self-corrects), whereas a missed web source would let an unendorsed web candidate
    /// reach Tier A — the one direction that produces the headline harm. No-op when the turn returned no web
    /// URLs (the proxy path, or a run that never searched the web).
    internal static void StampWebCitations(DiscoveryOutput o, IReadOnlyCollection<string> webCitationUrls)
    {
        if (webCitationUrls.Count == 0) return;
        var webHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in webCitationUrls)
            if (TryHost(u, out var h)) webHosts.Add(h);
        if (webHosts.Count == 0) return;

        for (var i = 0; i < o.Substances.Count; i++)
        {
            var s = o.Substances[i];
            if (!s.Citations.Any(c => WebHostOf(c, webHosts) is not null)) continue;
            var stamped = s.Citations
                .Select(c => WebHostOf(c, webHosts) is { } host
                          && !c.Source.StartsWith("web:", StringComparison.OrdinalIgnoreCase)
                    ? c with { Source = $"web:{host}" }
                    : c)
                .ToList();
            o.Substances[i] = s with { Citations = stamped };
        }
    }

    /// The captured web host this citation points at, or null. Matches when either field parses to an http(s)
    /// URL whose host is in the set, or textually contains one of the hosts (a bare "example.com/paper"
    /// reference with no scheme). Deliberately generous — see the stamping bias above.
    private static string? WebHostOf(Citation c, HashSet<string> webHosts)
    {
        foreach (var field in new[] { c.Reference, c.Source })
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            if (TryHost(field, out var host) && webHosts.Contains(host)) return host;
            foreach (var h in webHosts)
                if (field.Contains(h, StringComparison.OrdinalIgnoreCase)) return h;
        }
        return null;
    }

    private static bool TryHost(string s, out string host)
    {
        host = "";
        if (!string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0)
        {
            host = u.Host;
            return true;
        }
        return false;
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

            // RAIL 1 — the web may SUGGEST a marker; only the catalog and the reference corpus may ENDORSE
            // one. Tier A and `preferred` are endorsements. Enforced here, in code, rather than in the
            // prompt: Citation is four free-form strings and nothing else in the pipeline would ever notice.
            // "web:" is the prefix ToolBox.SearchWebAsync stamps on every hit ("web:<host>").
            var webOnly = s.Citations.All(c => c.Source.StartsWith("web:", StringComparison.OrdinalIgnoreCase));
            if (webOnly && s.Tier == "A")
                return $"candidate '{s.Element}/{s.Form}' is cited only by web sources and cannot be Tier A — " +
                       "corroborate it with search_catalog or search_reference, or tier it B with the limitation named in the rationale";
            if (webOnly && s.Preferred)
                return $"candidate '{s.Element}/{s.Form}' is cited only by web sources and cannot be marked preferred — " +
                       "a preferred form must rest on a catalog or reference source";

            // RAIL 2 — a CAS carries a check digit, so a transposed digit is PROVABLY wrong. A wrong CAS
            // clears the wrong substance through the regulatory gate, doses against the wrong molecular
            // weight, and gets ordered. This is the cheapest guard we have against the headline harm.
            if (!CasNumber.IsValid(s.Cas))
                return $"candidate '{s.Element}/{s.Form}' has CAS '{s.Cas}', which fails its check digit — " +
                       "re-read the CAS from a retrieved source; do not transcribe it from memory";
        }
        return null;
    }
}
