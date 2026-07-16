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
    public Task<DosingDoc?> GetDosingAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<DosingDoc>(RecordIds.Dosing(projectId), projectId, ct);
    public Task<CostDoc?> GetCostAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<CostDoc>(RecordIds.Cost(projectId), projectId, ct);
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

    /// The only CROSS-PARTITION query in this store — note the absent PartitionKey request option, which is
    /// what makes it one. The container is partitioned by /projectId, so "every project" is a fan-out by
    /// definition, and that is fine here rather than something to engineer around: one operator, projects in
    /// the tens, and the dashboard asks on mount and window focus rather than on a timer. The container takes
    /// the default indexing policy (infra/modules/data.bicep), so every path is indexed and both the `type`
    /// filter and the ORDER BY are index-served — a composite index is only needed for a multi-property sort.
    /// The cost is linear in project count; at thousands this would want a continuation token.
    ///
    /// Newest first, ordered on the STRING: CreatedAt is always DateTimeOffset.UtcNow.ToString("O"), so the
    /// offset is fixed-width and always +00:00 and lexicographic order IS chronological order.
    /// GetRevisionsAsync below leans on the same property.
    public async Task<IReadOnlyList<ProjectDoc>> ListProjectsAsync(CancellationToken ct = default)
    {
        var results = new List<ProjectDoc>();
        var query = container.GetItemLinqQueryable<ProjectDoc>()
            .Where(d => d.Type == RecordTypes.Project)
            .OrderByDescending(d => d.CreatedAt)
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
    ///
    /// UNBOUNDED, deliberately: one operator, one stage, a thread measured in tens of turns. But the caller
    /// re-renders this whole list into the agent's prompt on EVERY turn, so the ceiling is the model's
    /// context window, and blowing it is not graceful — a provider that silently truncates leaves the agent
    /// answering from an amputated conversation, which is exactly the kind of confidently-wrong output this
    /// system exists to prevent. The escape hatch, when a thread gets long enough to need one, is to
    /// summarise INTO THE RECORD (a `chat-summary` doc that this method returns in place of the turns it
    /// folds up) — never into in-memory session state, which cannot survive the multi-day re-entry (Law 6)
    /// that is the whole reason the thread is persisted at all.
    public async Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default)
    {
        var messages = new List<ChatMessageDoc>();
        var messageQuery = container.GetItemLinqQueryable<ChatMessageDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.ChatMessage && d.Stage == stage)
            .ToFeedIterator();
        while (messageQuery.HasMoreResults)
            messages.AddRange(await messageQuery.ReadNextAsync(ct));

        var replies = new List<ChatReplyDoc>();
        var replyQuery = container.GetItemLinqQueryable<ChatReplyDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.ChatReply && d.Stage == stage)
            .ToFeedIterator();
        while (replyQuery.HasMoreResults)
            replies.AddRange(await replyQuery.ReadNextAsync(ct));

        // The DOCS go to InOrder, not turns built here: it merges, maps and orders in one place, and it needs
        // both sides at once — a reply is positioned by the message it answers (ChatReplyDoc.MessageId), not by
        // its own clock. Sorting here rather than server-side buys nothing to give up: an ORDER BY per query
        // would still leave the two result sets to be merged in memory. Going through the SHARED function the
        // fake also calls is what stops the twins drifting on the one thing the transcript's meaning rests on.
        return ChatTurns.InOrder(messages, replies);
    }

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertDosingAsync(DosingDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertCostAsync(CostDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
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
