using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryRunStore : IRunStore
{
    private readonly ConcurrentDictionary<string, RunDoc> _runs = new();

    public Task UpsertAsync(RunDoc run, CancellationToken ct)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunDoc>>(
            [.. _runs.Values
                .Where(r => r.ProjectId == projectId && (stage is null || r.Stage == stage))
                .OrderBy(r => r.StartedAt, StringComparer.Ordinal)]);

    public Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run) && run.ProjectId == projectId ? run : null);
}
