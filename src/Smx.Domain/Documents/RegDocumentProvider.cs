using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smx.Domain.Documents;

/// The regulatory half of the catalog, over `reg-registry` (curated sources) and `reg-state`
/// (per-document change-detection state).
///
/// The load-bearing subtlety is that two DIFFERENT bronze layouts live behind one Cosmos container.
/// A synced document is written to `regulatory/{sourceId}/{docId}/{fetchTs}/raw.{ext}`; a
/// seed-imported one to `seed/{region}/{docId}/raw.txt`, with no timestamp segment, and its
/// reg-state LastFetchTs holds the sync date, which never appeared in the path. So the two cannot be
/// told apart from a path — they are told apart by whether the sourceId is a curated source, decided
/// here at catalog time rather than by probing storage.
public sealed class RegDocumentProvider(IRegDocumentSource source, IDocumentContentStore bronze)
{
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var sources = await source.ListSourcesAsync(ct);
        var docs = await source.ListDocsAsync(ct);
        var byId = sources.ToDictionary(s => s.SourceId, StringComparer.OrdinalIgnoreCase);

        return docs.Select(d =>
        {
            var curated = byId.GetValueOrDefault(d.SourceId);
            var title = curated?.Documents.FirstOrDefault(x => x.DocId == d.DocId)?.Title;
            return new DocumentSummary(
                Id: DocumentId.Encode(curated is null ? DocumentId.Seed : DocumentId.Reg, $"{d.SourceId}/{d.DocId}"),
                Kind: curated is null ? DocumentKinds.Seed : DocumentKinds.Reg,
                Title: title is { Length: > 0 } ? title : d.DocId,
                Subtitle: curated is null
                    ? $"seed / {d.SourceId} · official {d.OfficialDate}"
                    : $"{curated.Authority} · {curated.Regulation} · official {d.OfficialDate}",
                Available: true,
                State: DocumentStates.Available,
                ContentType: null,        // known only from the sidecar; the detail read fills it in
                OfficialDate: d.OfficialDate,
                IngestedUtc: d.LastFetchTs);
        }).ToList();
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;
        if (kind != DocumentId.Reg && kind != DocumentId.Seed) return null;

        var segments = DocumentId.SegmentsOf(kind, payload);
        var (sourceId, docId) = (segments[0], segments[1]);

        var doc = await source.GetDocAsync(docId, sourceId, ct);
        if (doc is null) return null;

        var sources = await source.ListSourcesAsync(ct);
        var curated = sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        // Kind and layout must agree. A `seed`-kinded id whose sourceId IS curated (or vice versa)
        // is a hand-edited id pointing at a path that does not exist; refuse rather than guess.
        if (kind == DocumentId.Reg && curated is null) return null;
        if (kind == DocumentId.Seed && curated is not null) return null;

        var folder = kind == DocumentId.Reg
            ? $"regulatory/{sourceId}/{docId}/{doc.LastFetchTs}"
            : $"seed/{sourceId}/{docId}";

        var meta = await ReadMetaAsync($"{folder}/meta.json", ct);
        var contentType = meta?.ContentType is { Length: > 0 } ? meta.ContentType
            : kind == DocumentId.Seed ? "text/plain" : "application/octet-stream";
        var blobPath = $"{folder}/raw.{ExtensionFor(contentType)}";

        var title = curated?.Documents.FirstOrDefault(x => x.DocId == docId)?.Title;
        var summary = new DocumentSummary(
            Id: documentId,
            Kind: kind == DocumentId.Reg ? DocumentKinds.Reg : DocumentKinds.Seed,
            Title: title is { Length: > 0 } ? title : docId,
            Subtitle: curated is null
                ? $"seed / {sourceId} · official {doc.OfficialDate}"
                : $"{curated.Authority} · {curated.Regulation} · official {doc.OfficialDate}",
            Available: true,
            State: DocumentStates.Available,
            ContentType: contentType,
            OfficialDate: doc.OfficialDate,
            IngestedUtc: doc.LastFetchTs);

        // "not recorded" rather than a plausible substitute — spec §3 invariant 6. For a seeded doc
        // the sidecar's httpStatus is 0 and contentType is hardcoded text/plain (SeedImporter.cs:111),
        // so the rail names it an import instead of implying an HTTP fetch that never happened.
        var provenance = new List<ProvenanceField>
        {
            new("Source URL", meta?.SourceUrl is { Length: > 0 } ? meta.SourceUrl : "not recorded", ProvenanceKinds.Url),
            new("Authority", curated?.Authority ?? "seed import"),
            new("Regulation", curated?.Regulation ?? sourceId),
            new("Official date", doc.OfficialDate),
            new("Origin", kind == DocumentId.Reg ? "monthly sync" : "seed import"),
            new(kind == DocumentId.Reg ? "Fetched" : "Imported", meta?.FetchTs is { Length: > 0 } ? meta.FetchTs : doc.LastFetchTs),
            new("Sync run", meta?.SyncRunId is { Length: > 0 } ? meta.SyncRunId : doc.SyncRunId),
            new("SHA-256", meta?.Sha256 is { Length: > 0 } ? meta.Sha256 : "not recorded", ProvenanceKinds.Hash),
            new("Content type", contentType),
        };
        if (kind == DocumentId.Reg)
            provenance.Add(new("HTTP status", meta is null ? "not recorded" : meta.HttpStatus.ToString()));

        return new DocumentDetail(summary, provenance, null, null, null, blobPath);
    }

    private async Task<BronzeMetaView?> ReadMetaAsync(string path, CancellationToken ct)
    {
        var bytes = await bronze.ReadAsync(path, ct);
        if (bytes is null) return null;
        try { return JsonSerializer.Deserialize<BronzeMetaView>(bytes, MetaJson); }
        catch (JsonException) { return null; }   // a corrupt sidecar reads as "not recorded", not a 500
    }

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// Mirrors BronzeIngestor.ExtensionFor — the mapping that produced these files in the first place.
    private static string ExtensionFor(string contentType) => contentType switch
    {
        var c when c.Contains("html", StringComparison.OrdinalIgnoreCase) => "html",
        var c when c.Contains("pdf", StringComparison.OrdinalIgnoreCase) => "pdf",
        var c when c.Contains("csv", StringComparison.OrdinalIgnoreCase) => "csv",
        var c when c.Contains("json", StringComparison.OrdinalIgnoreCase) => "json",
        var c when c.Contains("xml", StringComparison.OrdinalIgnoreCase) => "xml",
        var c when c.Contains("text/plain", StringComparison.OrdinalIgnoreCase) => "txt",
        _ => "bin",
    };

    private sealed record BronzeMetaView(
        string? SourceId, string? DocId, string? SourceUrl, string? OfficialDate, string? FetchTs,
        string? Sha256, string? ContentType, int HttpStatus, string? SyncRunId);
}
