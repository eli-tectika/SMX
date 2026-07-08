using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Data;

// Per-document sha256 change-detection state (reg-state).
public interface IRegStateStore
{
    Task<RegDocState?> GetAsync(string docId, string sourceId, CancellationToken ct);
    Task UpsertAsync(RegDocState state, CancellationToken ct);
}

// Staged/live/superseded regulatory chunks (reg-silver).
public interface IRegSilverStore
{
    Task UpsertStagedAsync(IReadOnlyList<SilverChunk> chunks, CancellationToken ct);
    Task PromoteStagedToLiveAsync(string runId, IReadOnlyList<string> changedDocIds, CancellationToken ct);
    Task DiscardStagedAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<SilverChunk>> GetStagedAsync(string runId, CancellationToken ct);
}

// Corpus-diff review record — audit for every run; status held only on anomaly (reg-review).
public interface IRegReviewStore
{
    Task UpsertAsync(ReviewRecord record, CancellationToken ct);
    Task<ReviewRecord?> GetAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<ReviewRecord>> GetByStatusAsync(string status, CancellationToken ct);
}

// Run log (reg-runs).
public interface IRegRunsStore
{
    Task UpsertAsync(SyncRun run, CancellationToken ct);
}

// The curated official-source registry (reg-registry), seeded from the git-versioned JSON.
public interface IRegRegistryStore
{
    Task<IReadOnlyList<RegSource>> GetEnabledAsync(CancellationToken ct);
    Task UpsertAsync(RegSource source, CancellationToken ct);
}
