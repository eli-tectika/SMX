using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly ConcurrentDictionary<string, object> _docs = new();
    public IReadOnlyCollection<object> Documents => (IReadOnlyCollection<object>)_docs.Values;

    public Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(projectId, out var d) ? (ProjectDoc?)d : null);
    public Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Constraints(projectId), out var d) ? (ConstraintsDoc?)d : null);
    public Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Matrix(projectId), out var d) ? (MatrixDoc?)d : null);
    public Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VerdictDoc>>(
            _docs.Values.OfType<VerdictDoc>().Where(v => v.ProjectId == projectId).ToList());

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
}
