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
    Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default);

    Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default);
    Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default);
    Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default);
}
