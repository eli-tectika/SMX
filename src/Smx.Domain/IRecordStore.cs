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
    Task<DecisionDoc?> GetDecisionAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default);
    Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default);
    Task<PoolDoc?> GetPoolAsync(string projectId, CancellationToken ct = default);
    Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default);
    Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default);
    Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default);

    /// The deliverable create_project writes into the project. Lives in `record` (per-project, on the
    /// audit trail) but is NOT a stage output — RecordDocRouter has an explicit arm ignoring it.
    Task<IntakeBriefDoc?> GetIntakeBriefAsync(string projectId, CancellationToken ct = default);

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
    Task UpsertDecisionAsync(DecisionDoc doc, CancellationToken ct = default);
    Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default);
    Task UpsertPoolAsync(PoolDoc doc, CancellationToken ct = default);
    Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default);
    Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default);
    Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default);
    Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default);
    Task UpsertIntakeBriefAsync(IntakeBriefDoc doc, CancellationToken ct = default);

    /// THE ONLY DELETE ON THIS INTERFACE, and it exists for one caller: a Discovery RE-RUN that replaces the
    /// CandidatesDoc orphans every verdict keyed to a candidate the new set no longer carries.
    ///
    /// Everything else in `record` is upserted by a deterministic id, so a re-run REPLACES its predecessor.
    /// Verdicts are the exception — one document per (cas, component) — so a smaller candidate set leaves
    /// documents behind that describe cells nobody is screening. Four readers already filter those out
    /// against the live cells (MatrixAssembler, EvidenceReview.Outstanding, the compliance-package export,
    /// ProjectTable.Build) and two do NOT: `GET /projects/{id}/verdicts` serves the partition raw, and
    /// PipelineRunner's Dosing folds `CompliantSet.Of(verdicts)` over all of them — so an orphan carrying
    /// `recommended` is dosed into a code for a substance the current analysis rejected. Filtering is repair
    /// at the read side, and it is one forgotten call site away from failing; removing the document is not.
    ///
    /// IDEMPOTENT: deleting a verdict that is not there is not an error. This runs after a replace, so the
    /// same prune re-executed on a retry must converge rather than throw.
    ///
    /// Nothing is lost that the audit needs: the run trail (a separate container, append-only) still records
    /// the screen that produced the verdict, and the prune names every document it removes on that trail.
    Task DeleteVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default);
}
