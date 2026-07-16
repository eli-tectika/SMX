namespace Smx.Domain;

/// Read-only view over the SDS library subsystem's corpus registry (the `sds-registry`
/// container, written by the regsync Functions app). The MSDS Registry surface composes this
/// corpus with the `msds-registry` governance overlay at read time — the corpus stays the
/// single source of truth for sheet facts (design §6.3: the registry "references the indexed
/// SDS; does not duplicate the corpus"), and no LLM is anywhere near this path.
public interface ISdsCorpusReader
{
    /// Current (indexed, non-superseded) sheets, optionally filtered by a case-insensitive
    /// substring over CAS / supplier. May contain multiple sheets per CAS.
    Task<IReadOnlyList<SdsCorpusSheet>> QuerySheetsAsync(string? search, CancellationToken ct = default);

    /// The latest current sheet for one CAS (by revision date, then ingestion time), or null.
    Task<SdsCorpusSheet?> GetLatestForCasAsync(string cas, CancellationToken ct = default);
}

/// One gathered SDS document, as the SDS subsystem recorded it at ingest time.
public sealed record SdsCorpusSheet(
    string Cas, string Supplier, string ProductName, string RevisionDate, string IngestedUtc);
