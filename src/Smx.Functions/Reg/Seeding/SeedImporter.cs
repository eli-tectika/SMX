using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Sds.Data;       // reuse IBronzeStore (ADLS)
using Smx.Functions.Sds.Ingestion;  // reuse IEmbedder

namespace Smx.Functions.Reg.Seeding;

// Per-document outcome of the seed import (isolated so one bad file never aborts the run).
public sealed record SeedDocResult(string Region, string DocId, string Result, int ChunkCount, string? Error);

// Aggregate result of a seed import — counts + the per-doc breakdown, returned as JSON by SeedImportHttp.
public sealed record SeedReport(
    int Docs, int Chunks, int Skipped, int Errors, IReadOnlyList<SeedDocResult> Results);

// One-time seed importer for the pre-collected local corpus (~100 docs across 13 region folders). Loads the
// bodies through the full medallion — Bronze (immutable raw + meta) → Silver (chunked + cited, written `live`
// with no review gate, since this is the authoritative baseline) → Gold (embedded + pushed to AI Search) — with
// ZERO network egress, unlike the monthly SyncPipeline. Deterministic ids ({docId}#{i}, sha, state key) make it
// idempotent: re-running merges rather than duplicates. Mirrors SyncPipeline.PromoteAsync for the embed→push→
// state-advance step so the monthly sync then treats these docs as a known baseline.
public sealed class SeedImporter
{
    private const string RunId = "seed";
    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    private readonly IBronzeStore _bronze;
    private readonly IRegSilverStore _silver;
    private readonly IRegStateStore _state;
    private readonly IEmbedder _embedder;
    private readonly IRegSearchClient _search;
    private readonly ILogger<SeedImporter> _log;

    public SeedImporter(IBronzeStore bronze, IRegSilverStore silver, IRegStateStore state,
        IEmbedder embedder, IRegSearchClient search, ILogger<SeedImporter> log)
    { _bronze = bronze; _silver = silver; _state = state; _embedder = embedder; _search = search; _log = log; }

    // Testable core (no trigger attribute), mirroring SyncPipeline.RunSyncAsync.
    public async Task<SeedReport> ImportAsync(string rootFolder, CancellationToken ct)
    {
        var results = new List<SeedDocResult>();
        int docs = 0, chunks = 0, skipped = 0, errors = 0;

        if (!Directory.Exists(rootFolder))
        {
            _log.LogWarning("Reg seed: root folder not found: {Root}", rootFolder);
            return new SeedReport(0, 0, 0, 1,
                new[] { new SeedDocResult("", "", DocResult.Error, 0, $"root folder not found: {rootFolder}") });
        }

        var fetchTs = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var syncDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        await _search.EnsureIndexAsync(ct);

        foreach (var regionDir in Directory.GetDirectories(rootFolder).OrderBy(d => d, StringComparer.Ordinal))
        {
            var region = Path.GetFileName(regionDir);
            if (string.Equals(region, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var file in Directory.GetFiles(regionDir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("._", StringComparison.Ordinal) ||
                    fileName.EndsWith("_metadata.txt", StringComparison.OrdinalIgnoreCase))
                { skipped++; continue; }

                var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                var docId = Slug(nameNoExt);
                try
                {
                    var count = await ImportDocAsync(regionDir, region, fileName, nameNoExt, docId, fetchTs, syncDate, ct);
                    docs++; chunks += count;
                    results.Add(new SeedDocResult(region, docId, DocResult.Added, count, null));
                }
                catch (Exception ex) // per-document isolation: one bad file does not fail the whole import
                {
                    errors++;
                    _log.LogError(ex, "Reg seed failed for {Region}/{Doc}", region, docId);
                    results.Add(new SeedDocResult(region, docId, DocResult.Error, 0, ex.Message));
                }
            }
        }

        _log.LogInformation("Reg seed complete: {Docs} docs, {Chunks} chunks, {Skipped} skipped, {Errors} errors",
            docs, chunks, skipped, errors);
        return new SeedReport(docs, chunks, skipped, errors, results);
    }

    private async Task<int> ImportDocAsync(string regionDir, string region, string fileName, string nameNoExt,
        string docId, string fetchTs, string syncDate, CancellationToken ct)
    {
        var sourceId = region; // region namespaces the doc; docId is the slugged file name
        var bytes = await File.ReadAllBytesAsync(Path.Combine(regionDir, fileName), ct);
        var sha = BronzeIngestor.Sha256Hex(bytes);
        var body = System.Text.Encoding.UTF8.GetString(bytes);

        // Provenance: prefer a `_metadata.txt` sidecar; else salvage from the body header.
        var metaFile = Path.Combine(regionDir, nameNoExt + "_metadata.txt");
        SeedMetadata? md = File.Exists(metaFile)
            ? MetadataReader.ParseMetadata(await File.ReadAllTextAsync(metaFile, ct))
            : null;
        var citation = MetadataReader.ToCitation(region, nameNoExt, md, MetadataReader.ParseBody(body));

        // Bronze: immutable raw + meta sidecar. Record the official PDF's filename as source provenance if present.
        var basePath = $"seed/{region}/{docId}";
        await _bronze.PutAsync($"{basePath}/raw.txt", bytes, ct);
        var bronzeMeta = new BronzeMeta(sourceId, docId, citation.SourceUrl, citation.OfficialDate,
            fetchTs, sha, "text/plain", 0, RunId);
        await _bronze.PutAsync($"{basePath}/meta.json", JsonSerializer.SerializeToUtf8Bytes(bronzeMeta, MetaJson), ct);
        var pdfName = nameNoExt + ".pdf";
        if (File.Exists(Path.Combine(regionDir, pdfName)))
            await _bronze.PutAsync($"{basePath}/source.pdf.txt", System.Text.Encoding.UTF8.GetBytes(pdfName), ct);

        // Silver: chunk the prose, write `live` directly (authoritative seed, no review gate).
        var texts = TextChunker.Chunk(body);
        var silver = SilverBuilder.Build(sourceId, docId, sha, RunId, syncDate, citation, texts, "live");
        await _silver.UpsertStagedAsync(silver, ct);

        // Gold: embed + push to AI Search (mirrors SyncPipeline.PromoteAsync).
        if (silver.Count > 0)
        {
            var vectors = await _embedder.EmbedAsync(silver.Select(c => c.Text).ToList(), ct);
            var gold = new List<GoldChunk>(silver.Count);
            for (var i = 0; i < silver.Count; i++)
            {
                var c = silver[i];
                gold.Add(new GoldChunk(c.Id, c.Text, vectors[i], c.Citation.Regulation, c.Citation.Authority,
                    c.SourceId, c.Citation.EntryId, c.DocId, c.Citation.SourceUrl, c.Citation.OfficialDate, c.SyncDate));
            }
            await _search.PushAsync(gold, ct);
        }

        // Advance change-detection state so the monthly sync treats this seeded doc as a known baseline.
        await _state.UpsertAsync(new RegDocState(docId, sourceId, sha, citation.OfficialDate, RunId, syncDate), ct);
        return silver.Count;
    }

    // Deterministic doc-id slug from a file name (stable across re-imports → idempotent).
    private static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "doc" : s;
    }
}
