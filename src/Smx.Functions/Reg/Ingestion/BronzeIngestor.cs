using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Sds.Data; // reuse IBronzeStore (ADLS)

namespace Smx.Functions.Reg.Ingestion;

// Result of fetching one registry document and staging it to Bronze. On "unchanged" (sha256 matches the last
// promoted state) nothing is written and Raw is null; on "added"/"changed" the raw artifact + meta sidecar are
// written immutably and Raw is returned for the parse/Silver stage.
public sealed record BronzeOutcome(string Result, string DocId, string SourceId, byte[]? Raw, string? Sha256, BronzeMeta? Meta);

// Bronze layer + sha256 change-detection. Writes are immutable (a new fetch_ts folder per changed fetch);
// state is NOT advanced here — it is advanced only on promotion (Phase 4), so a re-run before sign-off
// re-detects the same change rather than silently swallowing it.
public sealed class BronzeIngestor
{
    private readonly IBronzeStore _bronze;
    private readonly IRegStateStore _state;
    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    public BronzeIngestor(IBronzeStore bronze, IRegStateStore state)
    { _bronze = bronze; _state = state; }

    public async Task<BronzeOutcome> FetchAndStageAsync(
        RegSource source, RegDoc doc, IRegEgress egress, string runId, string fetchTs, CancellationToken ct)
    {
        var fetched = await egress.FetchAsync(new Uri(doc.Url), source.Headers, ct);
        if (fetched is null)
            return new BronzeOutcome(DocResult.Error, doc.DocId, source.SourceId, null, null, null);

        var sha = Sha256Hex(fetched.Content);
        var prev = await _state.GetAsync(doc.DocId, source.SourceId, ct);
        if (prev is not null && prev.Sha256 == sha)
            return new BronzeOutcome(DocResult.Unchanged, doc.DocId, source.SourceId, null, sha, null);

        // official_date is a parse-derived fact (it comes from the document body, not the HTTP layer), so it is
        // authoritatively set on the Silver citation. Bronze meta records fetch provenance; leave it empty here.
        var meta = new BronzeMeta(source.SourceId, doc.DocId, fetched.FinalUrl.ToString(), "",
            fetchTs, sha, fetched.ContentType, 200, runId);

        var ext = ExtensionFor(fetched.ContentType, doc.Url);
        var basePath = $"regulatory/{source.SourceId}/{doc.DocId}/{fetchTs}";
        await _bronze.PutAsync($"{basePath}/raw.{ext}", fetched.Content, ct);
        await _bronze.PutAsync($"{basePath}/meta.json", JsonSerializer.SerializeToUtf8Bytes(meta, MetaJson), ct);

        var result = prev is null ? DocResult.Added : DocResult.Changed;
        return new BronzeOutcome(result, doc.DocId, source.SourceId, fetched.Content, sha, meta);
    }

    public static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string ExtensionFor(string contentType, string url)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.Contains("csv")) return "csv";
        if (ct.Contains("json")) return "json";
        if (ct.Contains("xml")) return "xml";
        if (ct.Contains("html")) return "html";
        if (ct.Contains("pdf")) return "pdf";
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.');
        return string.IsNullOrEmpty(ext) ? "bin" : ext.ToLowerInvariant();
    }
}
