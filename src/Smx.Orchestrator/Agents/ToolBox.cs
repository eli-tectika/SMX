using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Agents;

public sealed class ToolBox(
    ICatalogLookup catalog,
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference,
    IKnowledgeStore knowledge,
    ILearnedConclusionsSearch learnedConclusions)
{
    public IList<AITool> DiscoveryTools() =>
    [
        AIFunctionFactory.Create(SearchCatalogAsync, "search_catalog",
            "List the catalog products (form, molecule, CAS, purity, supplier) available for an element from the SMX catalog. Use this to specify candidate forms and their CAS numbers; only propose candidates whose CAS you retrieved here."),
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
            "Search the cross-project Marker Library for a previously approved code that fits this application/material/objective. Prefer reusing a validated code over inventing a new one; cite the source project."),
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Search accumulated Learned Conclusions (prior material/regulatory findings with confidence + provenance) relevant to this intake. Treat them as prior evidence, not fact; a higher-confidence, more recent conclusion supersedes an older one."),
    ];

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

    public async Task<string> SearchMarkerLibraryAsync(string query, CancellationToken ct)
    {
        var markers = await knowledge.QueryMarkersAsync(query, ct);
        return markers.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior approved code fits; proceed without reuse, do not invent one\"}"
            : JsonSerializer.Serialize(new { results = markers }, Json.Options);
    }

    public async Task<string> SearchLearnedConclusionsAsync(string query, CancellationToken ct)
    {
        var chunks = await learnedConclusions.SearchAsync(query, 5, ct);
        return chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior conclusions on this; reason from primary sources, do not fabricate a prior finding\"}"
            : JsonSerializer.Serialize(new { results = chunks }, Json.Options);
    }

    public async Task<string> SearchRegulatoryAsync(string query, CancellationToken ct) => Render(await regulatory.SearchAsync(query, ct: ct));
    public async Task<string> SearchSdsAsync(string query, CancellationToken ct) => Render(await sds.SearchAsync(query, ct: ct));
    public async Task<string> SearchReferenceAsync(string query, CancellationToken ct) => Render(await reference.SearchAsync(query, ct: ct));

    private static string Render(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent facts; lower confidence or mark NeedsReview\"}"
            : JsonSerializer.Serialize(new { results = chunks }, Json.Options);
}
