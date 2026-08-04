namespace Smx.Domain.Documents;

/// A row of `sds-registry` (PK /cas) — a sheet that exists. Mirrors RegistryPointer, minus the
/// index-doc-id list the viewer has no use for.
public sealed record SdsSheetRow(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string Region, string Language, string SourceUrl, string BlobPath, bool Indexed,
    string IngestedUtc, string? SupersededBy, string? MasterListId);

/// A row of `sds-master-list` (PK /element) — a substance the system knows it needs a sheet for.
/// Status is pending | fetched | failed (SdsStatus in the Functions app). `awaiting_operator` was
/// deleted on 2026-07-29 (D5) and a migration rewrites the rows that carried it; a read can still
/// land before that migration runs, which is why the catalog keeps a branch for it.
///
/// `NextAttemptUtc` is nullable because it is the SCHEDULER's stamp, not the row's identity: a row
/// written before the backoff existed, or between the migration and the first sweep, genuinely has
/// no scheduled retry, and the surface must say that rather than invent one.
public sealed record SdsMasterRow(
    string Id, string Element, string Form, string Cas, string Status,
    string? LastAttemptUtc, int AttemptCount, string? NextAttemptUtc = null);

/// A row of `reg-state` (PK /sourceId, id = docId) — per-document change-detection state.
public sealed record RegDocRow(
    string DocId, string SourceId, string Sha256, string OfficialDate, string SyncRunId, string LastFetchTs);

/// A curated source from `reg-registry`. Membership here is what distinguishes a synced document
/// (`regulatory/` prefix) from a seed-imported one (`seed/` prefix).
public sealed record RegSourceRow(
    string SourceId, string Regulation, string Authority, IReadOnlyList<RegDocTitleRow> Documents);

public sealed record RegDocTitleRow(string DocId, string Url, string? Title);

/// A second read port over `sds-registry`, alongside the existing `ISdsCorpusReader` — deliberately,
/// not by oversight. `ISdsCorpusReader.QuerySheetsAsync` filters to current (indexed, non-superseded)
/// sheets, because its one consumer (Discovery's RAG lookup) must never cite a stale or unindexed
/// sheet. A file-viewer catalog has the opposite requirement: it must show a superseded sheet (as
/// history, with `State: Superseded`) and a not-yet-indexed one (an upload the operator just made),
/// so it needs every row, unfiltered — a narrower port cannot serve a wider consumer.
public interface ISdsDocumentSource
{
    Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default);
    Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default);
    Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default);
    Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default);
}

/// One certificate of analysis in bronze. Unlike a safety sheet there is no registry row behind it —
/// the file IS the record — so this carries only what storage itself knows.
///
/// That is deliberate, not a shortcut: the SDS side can drift (a registry row claiming a sheet that
/// storage does not have is the `blob-missing` case), and a catalog assembled straight from the
/// container cannot. The cost is that there is no curated title, supplier or CAS until a COA registry
/// exists; the surface says what it has rather than parsing them out of a filename.
public sealed record CoaRow(string FileName, string BlobPath, long SizeBytes, string? LastModifiedUtc);

public interface ICoaDocumentSource
{
    Task<IReadOnlyList<CoaRow>> ListAsync(CancellationToken ct = default);
    Task<CoaRow?> GetAsync(string fileName, CancellationToken ct = default);
}

public interface IRegDocumentSource
{
    Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default);
    Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default);
}
