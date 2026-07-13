using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

public sealed class CosmosRecordStore(Container container) : IRecordStore
{
    public Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<ProjectDoc>(projectId, projectId, ct);
    public Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<ConstraintsDoc>(RecordIds.Constraints(projectId), projectId, ct);
    public Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<MatrixDoc>(RecordIds.Matrix(projectId), projectId, ct);
    public Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<CandidatesDoc>(RecordIds.Candidates(projectId), projectId, ct);
    public Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default) =>
        ReadAsync<GateDoc>(RecordIds.Gate(projectId, gateType), projectId, ct);
    public Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default) =>
        ReadAsync<VerdictDoc>(RecordIds.Verdict(projectId, cas, componentId), projectId, ct);

    public async Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default)
    {
        var results = new List<VerdictDoc>();
        var query = container.GetItemLinqQueryable<VerdictDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.Verdict)
            .ToFeedIterator();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }

    public async Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default)
    {
        var results = new List<RevisionDoc>();
        var query = container.GetItemLinqQueryable<RevisionDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.Revision)
            .OrderBy(d => d.CreatedAt)   // the audit trail reads oldest-first
            .ToFeedIterator();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }

    public Task<ChatMessageDoc?> GetChatMessageAsync(string projectId, string id, CancellationToken ct = default) =>
        ReadAsync<ChatMessageDoc>(id, projectId, ct);

    /// Two queries, not one: the thread is a mixed sequence of two doc types, and each has to filter on its
    /// own `type` literal. There is no generic shortcut here — a `(dynamic)d` cast or an interface-typed
    /// member does not translate to SQL, it throws at query time.
    public async Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default)
    {
        var turns = new List<ChatTurn>();

        var messages = container.GetItemLinqQueryable<ChatMessageDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.ChatMessage && d.Stage == stage)
            .ToFeedIterator();
        while (messages.HasMoreResults)
            foreach (var m in await messages.ReadNextAsync(ct))
                turns.Add(new ChatTurn(ChatRoles.Operator, m.Text, m.CreatedAt, []));

        var replies = container.GetItemLinqQueryable<ChatReplyDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.ChatReply && d.Stage == stage)
            .ToFeedIterator();
        while (replies.HasMoreResults)
            foreach (var r in await replies.ReadNextAsync(ct))
                turns.Add(new ChatTurn(ChatRoles.Agent, r.Text, r.CreatedAt, r.ToolCalls));

        // Ordered here rather than server-side because the two result sets have to be merged anyway.
        // Ordinal, to match the ORDER BY Cosmos would have done — and the fake's.
        return turns.OrderBy(t => t.CreatedAt, StringComparer.Ordinal).ToList();
    }

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);

    private async Task<T?> ReadAsync<T>(string id, string pk, CancellationToken ct) where T : class
    {
        try { return (await container.ReadItemAsync<T>(id, new PartitionKey(pk), cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private Task Upsert<T>(T doc, string pk, CancellationToken ct) =>
        container.UpsertItemAsync(doc, new PartitionKey(pk), cancellationToken: ct);
}
