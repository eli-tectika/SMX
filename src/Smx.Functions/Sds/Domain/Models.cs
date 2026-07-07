namespace Smx.Functions.Sds.Domain;

public static class SdsStatus
{
    public const string Pending = "pending";
    public const string Fetched = "fetched";
    public const string Failed = "failed";
    public const string AwaitingOperator = "awaiting_operator";
}

public sealed record MasterListEntry(
    string Id, string Element, string Form, string Cas, string? SubstrateClass,
    string Status, string AddedBy, string AddedUtc, string? LastAttemptUtc, int AttemptCount);

public sealed record RegistryPointer(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string SourceUrl, string BlobPath, bool Indexed,
    IReadOnlyList<string> IndexDocIds, string IngestedUtc, string? SupersededBy, string MasterListId);

public sealed record SdsChunk(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string GhsSection, string Content,
    float[] ContentVector, string BlobPath, string MasterListId);

public sealed record AllowlistEntry(
    string Supplier, string Domain, int Priority, string Strategy,
    string SdsUrlTemplate, string? SearchUrlTemplate, string? ProductNumberRegex);

public sealed record SubstanceKey(string Element, string Form, string Cas);

public sealed record SourceCandidate(string Supplier, string Domain, Uri Url);

public sealed record SdsMetadata(
    string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string SourceUrl, string MasterListId);

public sealed record EgressResult(byte[] Content, string ContentType, Uri FinalUrl);

public sealed record ValidationResult(bool Ok, string? Reason);

public sealed record IngestResult(bool Ok, string? Reason, string? RegistryId);
