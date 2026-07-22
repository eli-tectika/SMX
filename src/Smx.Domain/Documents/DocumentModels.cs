namespace Smx.Domain.Documents;

public static class DocumentKinds
{
    public const string Sds = "sds";           // a safety data sheet, present or missing
    public const string Reg = "reg";           // a synced regulatory source document
    public const string Seed = "seed";         // a seed-imported regulatory document
    public const string All = "all";
}

public static class DocumentStates
{
    public const string Available = "available";
    public const string Missing = "missing";        // catalogued, no file — the gap rows
    public const string Superseded = "superseded";
    public const string All = "all";
}

public static class UnavailableReasons
{
    public const string NeverFetched = "never-fetched";   // gap row: no sheet was ever obtained
    public const string BlobMissing = "blob-missing";     // registry says yes, storage says no — real drift

    /// The registry's own compound key doesn't round-trip through DocumentId (an empty segment, most
    /// commonly — e.g. an operator upload with a blank field). Corrupt data, not absent data: the row
    /// is real, but no id can ever be minted for it that TryDecode would accept, so it cannot be
    /// resolved by primary key. Surfaced at list time (SdsDocumentProvider), not via GetAsync — a
    /// malformed id must never touch storage regardless of who produced it (spec §3 invariant 2), so
    /// there is no way to look this row back up through the id alone.
    public const string UnresolvableId = "unresolvable-id";
}

/// One catalog row. `Kind` is the FACET (sds/reg/seed) — note a gap row reports `sds`, because it is a
/// safety data sheet that is missing, not a fourth category. Only DocumentId carries the `sdsgap`
/// distinction, and only because resolution targets a different container.
public sealed record DocumentSummary(
    string Id,
    string Kind,
    string Title,
    string Subtitle,
    bool Available,
    string State,
    string? ContentType,
    string? OfficialDate,
    string? IngestedUtc);

/// A labelled provenance line. A LIST rather than a fixed record because SDS and regulatory provenance
/// genuinely differ in shape, and the rail renders whatever it is handed.
public sealed record ProvenanceField(string Label, string Value, string Kind = "text");

public static class ProvenanceKinds
{
    public const string Text = "text";
    public const string Url = "url";
    public const string Hash = "hash";
}

public sealed record DocumentDetail(
    DocumentSummary Summary,
    IReadOnlyList<ProvenanceField> Provenance,
    string? UnavailableReason,
    string? UnavailableDetail,
    string? SupersededById,
    string? BlobPath);   // never serialised to the client; the endpoints use it to fetch

public sealed record DocumentChunk(int Ordinal, string Text, string? EntryId, string? Section);

public sealed record DocumentFilter(string Kind = DocumentKinds.All, string? Q = null, string State = DocumentStates.All);
