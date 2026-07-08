using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Agents;

public sealed class ToolBox(
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference)
{
    public IList<AITool> ScreeningTools() =>
    [
        AIFunctionFactory.Create(LookupCompatibilityAsync, "lookup_compatibility",
            "Exact tabulated element×substrate compatibility verdict from the SMX knowledge base. Call this FIRST for the Compatibility dimension; only reason from search results when the pair is not tabulated."),
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
    ];

    public async Task<string> LookupCompatibilityAsync(string element, string substrate, CancellationToken ct)
    {
        var card = await compatibility.LookupAsync(element, substrate, ct);
        return card is null
            ? $"{{\"tabulated\":false,\"note\":\"{element}×{substrate} not tabulated — reason from search results and mark confidence accordingly\"}}"
            : JsonSerializer.Serialize(new { tabulated = true, card }, Json.Options);
    }

    public async Task<string> SearchRegulatoryAsync(string query, CancellationToken ct) => Render(await regulatory.SearchAsync(query, ct: ct));
    public async Task<string> SearchSdsAsync(string query, CancellationToken ct) => Render(await sds.SearchAsync(query, ct: ct));
    public async Task<string> SearchReferenceAsync(string query, CancellationToken ct) => Render(await reference.SearchAsync(query, ct: ct));

    private static string Render(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent facts; lower confidence or mark NeedsReview\"}"
            : JsonSerializer.Serialize(new { results = chunks }, Json.Options);
}
