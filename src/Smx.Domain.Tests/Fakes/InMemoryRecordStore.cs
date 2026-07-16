using System.Collections.Concurrent;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// <summary>
/// The test twin of CosmosRecordStore.
///
/// Every doc is DEEP-COPIED through <see cref="Json.Options"/> on the way in and on the way out. That is not
/// defensive tidiness — it is the only way this fake can tell the truth about a read-modify-write. Cosmos
/// round-trips through JSON: an upsert SNAPSHOTS the doc and a read hands back a FRESH graph. A dictionary of
/// live references does neither, and then code that mutates a doc and *forgets to upsert it* still appears to
/// persist the change here (it is the same object) while in Cosmos the change is simply lost. An idempotency
/// test would go green against a dispatcher that, in production, leaves the message `pending`, gets it
/// redelivered by the at-least-once change feed, and re-runs the turn — queueing a second revision and a
/// second Learned Conclusion. Copying through the production options also gives the fake production's exact
/// JSON semantics (naming policy, enum handling, null omission) for free.
/// </summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly ConcurrentDictionary<string, object> _docs = new();
    public IReadOnlyCollection<object> Documents => (IReadOnlyCollection<object>)_docs.Values;

    private static T Copy<T>(T doc) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;

    /// A typed point-read: the id must exist AND hold a doc of the requested type — a Cosmos read of an id
    /// whose document is some other type would not deserialize into T either.
    private Task<T?> Read<T>(string id) where T : class =>
        Task.FromResult(_docs.TryGetValue(id, out var d) && d is T typed ? Copy(typed) : null);

    private Task Write<T>(T doc, string id) { _docs[id] = Copy(doc)!; return Task.CompletedTask; }

    public Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        Read<ProjectDoc>(projectId);

    /// Ordinal, descending — the twin of the Cosmos ORDER BY. Both sort the raw "O"-format string rather
    /// than a parsed instant, which is only correct because that format is fixed-width and always +00:00.
    /// Unbounded, like the real store: no Take, so a test cannot pass against a cap the store does not have.
    public Task<IReadOnlyList<ProjectDoc>> GetProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectDoc>>(_docs.Values.OfType<ProjectDoc>()
            .OrderByDescending(p => p.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos ORDER BY ... DESC
            .Select(Copy)
            .ToList());
    public Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default) =>
        Read<ConstraintsDoc>(RecordIds.Constraints(projectId));
    public Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default) =>
        Read<MatrixDoc>(RecordIds.Matrix(projectId));
    public Task<DosingDoc?> GetDosingAsync(string projectId, CancellationToken ct = default) =>
        Read<DosingDoc>(RecordIds.Dosing(projectId));
    public Task<CostDoc?> GetCostAsync(string projectId, CancellationToken ct = default) =>
        Read<CostDoc>(RecordIds.Cost(projectId));
    public Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VerdictDoc>>(
            _docs.Values.OfType<VerdictDoc>().Where(v => v.ProjectId == projectId).Select(Copy).ToList());
    public Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default) =>
        Read<CandidatesDoc>(RecordIds.Candidates(projectId));
    public Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default) =>
        Read<GateDoc>(RecordIds.Gate(projectId, gateType));
    public Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default) =>
        Read<VerdictDoc>(RecordIds.Verdict(projectId, cas, componentId));
    public Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RevisionDoc>>(_docs.Values.OfType<RevisionDoc>()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos ORDER BY
            .Select(Copy)
            .ToList());

    /// Unlike every other point-read this one takes a raw id instead of deriving it, so a mismatched
    /// (projectId, id) pair is constructible — and Cosmos's partition-scoped read returns null for one.
    /// The ProjectId check is what keeps the twins agreeing on that.
    public Task<ChatMessageDoc?> GetChatMessageAsync(string projectId, string id, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(id, out var d) && d is ChatMessageDoc m && m.ProjectId == projectId
            ? Copy(m) : null);

    /// Sorted through the same ChatTurns.InOrder the Cosmos store calls — the merge order is one function,
    /// not two agreeing implementations.
    public Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default) =>
        Task.FromResult(ChatTurns.InOrder(
            _docs.Values.OfType<ChatMessageDoc>()
                .Where(m => m.ProjectId == projectId && m.Stage == stage)
                .Select(Copy),
            _docs.Values.OfType<ChatReplyDoc>()
                .Where(r => r.ProjectId == projectId && r.Stage == stage)
                .Select(Copy)));   // Copy, not the stored reference: Cosmos hands back a fresh graph, so the
                                   // turn's ToolCalls must not alias the list the stored reply still owns.

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertDosingAsync(DosingDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertCostAsync(CostDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
    public Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default) => Write(doc, doc.Id);
}
