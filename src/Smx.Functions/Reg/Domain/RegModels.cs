namespace Smx.Functions.Reg.Domain;

// ── Registry of official sources (curated; never open web) ──────────────────────────────────────────
// One entry per regulation/source. `Documents` enumerates the concrete artifacts fetched for that source
// (one for a single dataset like OEHHA; several for ECHA lists). `Domain` feeds the egress allowlist.
// `Id` is the Cosmos document id (= SourceId); the registry provider fills it after loading the seed JSON,
// so the git-versioned file need not repeat it. `ParserConfig` carries optional per-source hints (e.g. CSV
// column-name mappings for GenericCsvParser) so a new dataset source can be onboarded by config, not code.
public sealed record RegSource(
    string SourceId, string Regulation, string Authority, string AccessMethod, string Domain,
    string Parser, bool Enabled, int Version, IReadOnlyList<RegDoc> Documents, string? Id = null,
    IReadOnlyDictionary<string, string>? ParserConfig = null, string? Cadence = null,
    // Optional per-source request headers (e.g. EUR-Lex Cellar content negotiation:
    // Accept: application/xhtml+xml, Accept-Language: eng) applied per-request by the egress client.
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record RegDoc(string DocId, string Url, string? Title);

// ── Citation carried by every chunk (correctness: every verdict traces to a cited source + dates) ────
public sealed record Citation(
    string Regulation, string Authority, string? EntryId, string? ArticleOrAnnex,
    string SourceUrl, string OfficialDate);

// ── Bronze sidecar metadata (written next to the immutable raw artifact) ─────────────────────────────
public sealed record BronzeMeta(
    string SourceId, string DocId, string SourceUrl, string OfficialDate, string FetchTs,
    string Sha256, string ContentType, int HttpStatus, string SyncRunId);

// ── Silver chunk (Cosmos reg-silver, pk /docId) — parsed + chunked, one doc per chunk ────────────────
public sealed record SilverChunk(
    string Id, string SourceId, string DocId, int ChunkIndex, string Text, Citation Citation,
    string DocSha256, string SyncRunId, string SyncDate, string Status);

// ── Gold document (AI Search regulatory-corpus index) — flat, with the embedding vector ──────────────
public sealed record GoldChunk(
    string Id, string Content, float[] ContentVector, string Regulation, string Authority,
    string SourceId, string? EntryId, string DocId, string SourceUrl, string OfficialDate, string SyncDate);

// ── Per-document change-detection state (Cosmos reg-state, pk /sourceId, id = docId) ─────────────────
public sealed record RegDocState(
    string Id, string SourceId, string Sha256, string OfficialDate, string SyncRunId, string LastFetchTs);

// ── Diff + anomaly assessment produced at the end of a sweep ─────────────────────────────────────────
public sealed record DocOutcome(string SourceId, string DocId, string Result, int ChunkCount, string? Error);

public sealed record AnomalyAssessment(bool Anomalous, IReadOnlyList<string> Reasons);

public sealed record CorpusDiff(
    string SyncRunId, int Added, int Changed, int Unchanged, int Errors,
    IReadOnlyList<string> ChangedDocIds, AnomalyAssessment Anomaly);

// ── Review record (Cosmos reg-review, pk /syncRunId) — audit for every run; held only on anomaly ─────
public sealed record ReviewRecord(
    string Id, string SyncRunId, CorpusDiff Diff, string Status,
    string? DecisionKind, OperatorSignoff? Signoff, string CreatedUtc, string? ExpiresAt);

public sealed record OperatorSignoff(string By, string DecidedUtc, string? Reason);

public sealed record ReviewDecisionRequest(string Decision, string SignoffBy, string? Reason);

// ── Run log (Cosmos reg-runs, pk /syncRunId) ─────────────────────────────────────────────────────────
public sealed record SyncRun(
    string Id, string SyncRunId, string Status, int Added, int Changed, int Unchanged, int Errors,
    string StartedUtc, string? FinishedUtc, IReadOnlyList<string> ErrorDetails);

public static class RegStatus
{
    public const string AutoPromoted = "auto-promoted";
    public const string Held = "held";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string HeldExpired = "held-expired";
}

public static class DocResult
{
    public const string Added = "added";
    public const string Changed = "changed";
    public const string Unchanged = "unchanged";
    public const string Error = "error";
}
