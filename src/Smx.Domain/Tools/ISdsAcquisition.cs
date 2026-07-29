namespace Smx.Domain.Tools;

public static class SdsEnsureStatus
{
    /// A current sheet was already indexed — returned with no egress at all.
    public const string AlreadyHad = "already-had";
    public const string Fetched = "fetched";
    /// Tried and could not get it. A normal answer, not an error: the caller reads Attempted to learn why.
    public const string Unavailable = "unavailable";
}

public sealed record SdsAttempt(string Url, string Supplier, string Outcome);

public sealed record SdsEnsureResult(
    string Status,
    string? RegistryId = null,
    string? Supplier = null,
    string? RevisionDate = null,
    string? Reason = null,
    IReadOnlyList<SdsAttempt>? Attempted = null)
{
    public IReadOnlyList<SdsAttempt> Attempts => Attempted ?? [];
    public bool Have => Status is SdsEnsureStatus.AlreadyHad or SdsEnsureStatus.Fetched;
}

/// One sheet the operator supplied by hand, on its way to the same ingestion pipeline every fetched
/// sheet goes through. Supplier and revision date are NOT decoration: they are two thirds of the
/// registry's compound key, and a blank one mints an id that nothing can ever open again.
public sealed record SdsUpload(
    string Cas, string Supplier, string ProductName, string RevisionDate, byte[] Pdf);

/// The upload's verdict. `Ok: false` is a normal answer — the same content validation every fetched
/// sheet faces (the requested CAS appears in the text, ≥10 GHS sections parse) applies to an
/// operator's file too. An upload is a fallback, not an override: a document that is not a safety
/// sheet for this substance does not become one by arriving through a file picker.
public sealed record SdsUploadResult(bool Ok, string? Reason = null, string? RegistryId = null);

/// The backend's half of SDS acquisition. The `regsync` Function App does the fetching — it sits on the
/// only subnet with a NAT gateway and holds the Bronze / AI Search / Cosmos write grants — and this is the
/// line to it.
///
/// Lives in Domain rather than Infrastructure so ToolBox and PipelineRunner depend on the capability, not
/// on an HTTP client.
public interface ISdsAcquisition
{
    /// Fetch and index the sheet for a CAS, synchronously and within a bounded budget. NEVER throws and
    /// never blocks a stage: an unobtainable sheet comes back as Unavailable with the attempts recorded,
    /// which the caller is free to proceed past. That is the whole point of the 2026-07-29 redesign — an
    /// agent that discovers a missing sheet used to have to park and wait days for a cron.
    Task<SdsEnsureResult> EnsureAsync(string cas, string? element, string? form, CancellationToken ct);

    /// Record that a substance is in play, so the scheduled sweep will chase its sheet even if nobody asks
    /// for it now. Bookkeeping: it must never fail a caller, so implementations swallow and log.
    Task AppendAsync(string element, string form, string cas, CancellationToken ct);

    /// Ingest a sheet the operator supplied. The fallback for the substance nobody publishes a fetchable
    /// PDF for — never a gate, and never a way around one: the uploaded file is validated by content
    /// exactly like a fetched one. Like EnsureAsync, a failure comes back as a result rather than an
    /// exception; only cancellation throws.
    Task<SdsUploadResult> UploadAsync(SdsUpload upload, CancellationToken ct);
}
