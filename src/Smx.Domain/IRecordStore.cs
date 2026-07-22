using Smx.Domain.Records;

namespace Smx.Domain;

public interface IRecordStore
{
    Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default);

    /// The one accessor with no projectId: every other read here is partition-scoped by design, and this one
    /// fans out because "which projects exist" is the question a client cannot ask any other way — without it
    /// a project id is discoverable only to whoever just created it.
    ///
    /// Returns EVERY project, deliberately unbounded. A cap would silently drop the oldest, and this list is
    /// the only route to a project and the source of the "Needs signing" card. See
    /// CosmosRecordStore.GetProjectsAsync, which bounds the page size instead.
    Task<IReadOnlyList<ProjectDoc>> GetProjectsAsync(CancellationToken ct = default);
    Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default);
    Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default);
    Task<DosingDoc?> GetDosingAsync(string projectId, CancellationToken ct = default);
    Task<CostDoc?> GetCostAsync(string projectId, CancellationToken ct = default);
    Task<DecisionDoc?> GetDecisionAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default);
    Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default);
    Task<PoolDoc?> GetPoolAsync(string projectId, CancellationToken ct = default);
    Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default);
    Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default);
    Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default);

    /// The persisted per-stage conversation, oldest-first. This IS the thread: the MAF agent session is
    /// in-memory and cannot be rehydrated, so the record is the only thing that survives a restart or a
    /// multi-day re-entry (Law 6).
    Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default);
    Task<ChatMessageDoc?> GetChatMessageAsync(string projectId, string id, CancellationToken ct = default);

    Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default);
    Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default);
    Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default);
    Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default);
    Task UpsertDosingAsync(DosingDoc doc, CancellationToken ct = default);
    Task UpsertCostAsync(CostDoc doc, CancellationToken ct = default);
    Task UpsertDecisionAsync(DecisionDoc doc, CancellationToken ct = default);
    Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default);
    Task UpsertPoolAsync(PoolDoc doc, CancellationToken ct = default);
    Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default);
    Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default);
    Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default);
    Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default);
}
