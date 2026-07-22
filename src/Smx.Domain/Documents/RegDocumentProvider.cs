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
            var kind = curated is null ? DocumentKinds.Seed : DocumentKinds.Reg;
            var id = DocumentId.Encode(kind, $"{d.SourceId}/{d.DocId}");
            var title = curated?.Documents.FirstOrDefault(x => x.DocId == d.DocId)?.Title;
            // ContentType is known only from the sidecar; the detail read (GetAsync) fills it in.
            return Summarize(id, kind, d.SourceId, d.DocId, curated, title, d.OfficialDate, d.LastFetchTs,
                contentType: null);
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

        // LastFetchTs is a reg-state field DocumentId never sees or validates, yet it becomes a
        // folder segment two lines down. Not reachable from the API today — DocumentId already gates
        // the id itself, and this value never left the state store — but the class comment above
        // calls DocumentId THE boundary, and this is the one place that builds a path from a value
        // DocumentId had no chance to refuse. Reject rather than trust it.
        if (kind == DocumentId.Reg && HasPathTraversal(doc.LastFetchTs)) return null;

        var folder = kind == DocumentId.Reg
            ? $"regulatory/{sourceId}/{docId}/{doc.LastFetchTs}"
            : $"seed/{sourceId}/{docId}";

        var meta = await ReadMetaAsync($"{folder}/meta.json", ct);
        var docTitle = curated?.Documents.FirstOrDefault(x => x.DocId == docId);

        string extension;
        string? publishedContentType;   // what we tell the client — never a guess (spec §3 invariant 6)
        if (kind == DocumentId.Seed)
        {
            // SeedImporter always writes raw.txt (SeedImporter.cs:110) and hardcodes contentType
            // "text/plain" (SeedImporter.cs:111) — for a seeded doc these are facts of the fixed
            // layout, not a guess, so "text/plain" stays the default even with no sidecar.
            extension = "txt";
            publishedContentType = meta?.ContentType is { Length: > 0 } ? meta.ContentType : "text/plain";
        }
        else
        {
            // The sidecar may be missing. "application/octet-stream" here exists ONLY to pick a file
            // extension to open — it must never also be published as if it were recorded fact, which
            // is why publishedContentType (below) stays null instead of repeating this guess.
            var guessedContentType = meta?.ContentType is { Length: > 0 } ? meta.ContentType : "application/octet-stream";
            extension = ExtensionFor(guessedContentType, docTitle?.Url ?? "");
            publishedContentType = meta?.ContentType is { Length: > 0 } ? meta.ContentType : null;
        }
        var blobPath = $"{folder}/raw.{extension}";

        var summary = Summarize(documentId, kind == DocumentId.Reg ? DocumentKinds.Reg : DocumentKinds.Seed,
            sourceId, docId, curated, docTitle?.Title, doc.OfficialDate, doc.LastFetchTs, publishedContentType);

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
            new("Content type", publishedContentType ?? "not recorded"),
        };
        if (kind == DocumentId.Reg)
            provenance.Add(new("HTTP status", meta is null ? "not recorded" : meta.HttpStatus.ToString()));

        return new DocumentDetail(summary, provenance, null, null, null, blobPath);
    }

    // Shared by ListAsync and GetAsync so a list row and its own detail view cannot drift apart on a
    // traceability surface — they used to build Title/Subtitle independently and already differed in
    // how they located the curated source (a dictionary in the list, a FirstOrDefault in the detail).
    private static DocumentSummary Summarize(
        string id, string kind, string sourceId, string docId, RegSourceRow? curated, string? title,
        string officialDate, string lastFetchTs, string? contentType) => new(
        Id: id,
        Kind: kind,
        Title: title is { Length: > 0 } ? title : docId,
        Subtitle: curated is null
            ? $"seed / {sourceId} · official {officialDate}"
            : $"{curated.Authority} · {curated.Regulation} · official {officialDate}",
        Available: true,
        State: DocumentStates.Available,
        ContentType: contentType,
        OfficialDate: officialDate,
        IngestedUtc: lastFetchTs);

    private static bool HasPathTraversal(string s) =>
        s.Contains('/') || s.Contains('\\') || s.Contains("..", StringComparison.Ordinal);

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

    /// Mirrors BronzeIngestor.ExtensionFor's branch order VERBATIM — that order is not incidental.
    /// application/xhtml+xml contains both "html" and "xml" as substrings, and the ingestor's
    /// csv → json → xml → html → pdf order is what makes it resolve to "xml", which is the file the
    /// ingestor actually wrote. Four curated EUR-Lex sources send that Accept header (enabled:false
    /// today — this is latent, not hypothetical, until one of them goes live). Falling back to the
    /// curated doc's own URL extension, exactly as the ingestor does, matters too: a source served
    /// as application/octet-stream with a plainly-.csv URL is common and reachable now.
    ///
    /// This whole method re-derives a filename BronzeIngestor already computed once, at fetch time,
    /// and then discarded — nothing persists it. The durable fix is for BronzeIngestor to write the
    /// chosen extension into meta.json when it writes raw.{ext}; that is a Functions-side ingest
    /// change and out of scope for a read-only viewer.
    private static string ExtensionFor(string contentType, string docUrl)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.Contains("csv")) return "csv";
        if (ct.Contains("json")) return "json";
        if (ct.Contains("xml")) return "xml";
        if (ct.Contains("html")) return "html";
        if (ct.Contains("pdf")) return "pdf";
        // Uri.TryCreate rather than `new Uri(docUrl)`: unlike the ingestor (which just fetched this
        // exact URL, so it is guaranteed well-formed), this is a read path over a curated title the
        // doc row might not even carry — a missing or malformed URL must fall through to "bin", not throw.
        var ext = Uri.TryCreate(docUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).TrimStart('.')
            : "";
        return string.IsNullOrEmpty(ext) ? "bin" : ext.ToLowerInvariant();
    }

    private sealed record BronzeMetaView(
        string? SourceId, string? DocId, string? SourceUrl, string? OfficialDate, string? FetchTs,
        string? Sha256, string? ContentType, int HttpStatus, string? SyncRunId);
}
