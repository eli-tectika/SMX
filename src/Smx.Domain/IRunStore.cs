using Smx.Domain.Records;

namespace Smx.Domain;

/// Persistence for the run trail. Separate from IRecordStore because the two containers are separate
/// on purpose: nothing that reads project state should ever page through telemetry.
public interface IRunStore
{
    Task UpsertAsync(RunDoc run, CancellationToken ct = default);

    /// Every run for the project, oldest first. `stage` null ⇒ all stages.
    Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct = default);

    Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct = default);
}
