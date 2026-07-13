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
    public Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Candidates(projectId), out var d) ? (CandidatesDoc?)d : null);
    public Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Gate(projectId, gateType), out var d) ? (GateDoc?)d : null);
    public Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Verdict(projectId, cas, componentId), out var d) ? (VerdictDoc?)d : null);
    public Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RevisionDoc>>(_docs.Values.OfType<RevisionDoc>()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos ORDER BY
            .ToList());

    public Task<ChatMessageDoc?> GetChatMessageAsync(string projectId, string id, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(id, out var d) ? d as ChatMessageDoc : null);
    public Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatTurn>>(
            _docs.Values.OfType<ChatMessageDoc>()
                .Where(m => m.ProjectId == projectId && m.Stage == stage)
                .Select(m => new ChatTurn(ChatRoles.Operator, m.Text, m.CreatedAt, []))
            .Concat(_docs.Values.OfType<ChatReplyDoc>()
                .Where(r => r.ProjectId == projectId && r.Stage == stage)
                .Select(r => new ChatTurn(ChatRoles.Agent, r.Text, r.CreatedAt, r.ToolCalls)))
            .OrderBy(t => t.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos-side merge sort
            .ToList());

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
}
