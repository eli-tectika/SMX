using Smx.Domain.Records;

namespace Smx.Domain;

public interface IRecordStore
{
    Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default);
    Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default);
    Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default);
    Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default);
    Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default);
    Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default);
    Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default);

    Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default);
    Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default);
    Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default);
    Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default);
    Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default);
    Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default);
    Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default);
}
