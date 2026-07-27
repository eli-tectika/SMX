using System.Collections.Concurrent;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// <summary>
/// The test twin of CosmosRunStore.
///
/// Deep-copied through <see cref="Json.Options"/> on the way in and out, exactly like
/// InMemoryRecordStore — and it matters MORE here than there: <see cref="RunDoc.Append"/> mutates
/// <see cref="RunDoc.Steps"/> in place. A dictionary of live references would let a caller that appends
/// a step and forgets to re-upsert it look perfectly correct against this fake (it's the same object)
/// while in Cosmos the appended step never left the process — exactly the failure this feature exists
/// to catch: a run trail that silently went missing.
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly ConcurrentDictionary<string, RunDoc> _runs = new();

    private static RunDoc Copy(RunDoc run) =>
        JsonSerializer.Deserialize<RunDoc>(JsonSerializer.Serialize(run, Json.Options), Json.Options)!;

    public Task UpsertAsync(RunDoc run, CancellationToken ct = default)
    {
        _runs[run.Id] = Copy(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunDoc>>(
            [.. _runs.Values
                .Where(r => r.ProjectId == projectId && (stage is null || r.Stage == stage))
                .OrderBy(r => r.StartedAt, StringComparer.Ordinal)
                .Select(Copy)]);

    public Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run) && run.ProjectId == projectId ? Copy(run) : null);
}
