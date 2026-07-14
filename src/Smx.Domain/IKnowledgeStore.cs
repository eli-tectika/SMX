using Smx.Domain.Records;

namespace Smx.Domain;

/// Cross-project knowledge layer (design §6). Separate from IRecordStore: these containers are NOT
/// on the per-project `record` change-feed bus. `Query*` is a case-insensitive substring browse.
public interface IKnowledgeStore
{
    Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default);
    Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default);
    Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default);

    Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default);

    /// Structured reuse lookup for the Intake agent: ANDs the provided dimensions (a null/blank
    /// dimension is not constrained) and returns only markers still approved for reuse. Distinct from
    /// QueryMarkersAsync, which is the operator's free-text browse: a combined phrase like
    /// "anti-counterfeit label overt" is a substring of NO single field, so a substring browse cannot
    /// serve the agent's per-dimension reuse question.
    Task<IReadOnlyList<MarkerLibraryDoc>> FindMarkersAsync(string? application, string? material, string? objective, CancellationToken ct = default);

    Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default);

    Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default);
    Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default);
    Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default);

    /// Metal loading, keyed by CAS and shared by every project (design §6). Null on a cold store —
    /// Dosing parks and asks the operator, and the answer is kept forever.
    Task<SubstancePropertyDoc?> GetSubstancePropertyAsync(string cas, CancellationToken ct = default);
    Task UpsertSubstancePropertyAsync(SubstancePropertyDoc doc, CancellationToken ct = default);
}
