// src/Smx.Functions/Reference/Seeding/ReferenceSeeder.cs
using Smx.Functions.Reference.Config;
using Smx.Functions.Reference.Data;
using Smx.Functions.Reference.Domain;
using Smx.Functions.Reference.Ingestion;
using Smx.Functions.Sds.Ingestion; // IEmbedder (reused)

namespace Smx.Functions.Reference.Seeding;

public sealed class ReferenceSeeder
{
    private readonly IReferenceStore _store;
    private readonly IEmbedder _embedder;
    private readonly IReferenceSearchClient _search;
    private readonly ReferenceOptions _opts;

    public ReferenceSeeder(IReferenceStore store, IEmbedder embedder,
        IReferenceSearchClient search, ReferenceOptions opts)
    { _store = store; _embedder = embedder; _search = search; _opts = opts; }

    public async Task<SeedReport> SeedAsync(SeedData data, CancellationToken ct)
    {
        foreach (var d in data.Compatibility)
            await _store.UpsertAsync(_opts.CompatibilityContainer, d, d.Element, ct);
        foreach (var d in data.Bibliography)
            await _store.UpsertAsync(_opts.BibliographyContainer, d, d.RefId, ct);
        foreach (var d in data.Suppliers)
            await _store.UpsertAsync(_opts.SuppliersContainer, d, d.Supplier, ct);
        foreach (var d in data.Catalog)
            await _store.UpsertAsync(_opts.CatalogContainer, d, d.Element, ct);

        await _search.EnsureIndexAsync(ct);
        if (data.Chunks.Count > 0)
        {
            var vectors = await _embedder.EmbedAsync(data.Chunks.Select(c => c.Content).ToList(), ct);
            var chunks = data.Chunks.Select((c, i) => new ReferenceChunk(
                c.Id, c.Content, vectors[i], c.Element, c.Substrate, c.Dimension, c.Verdict,
                c.RefIds, c.SourceTitle, c.Doi, c.Url, c.Sheet, c.Dataset)).ToList();
            await _search.PushAsync(chunks, ct);
        }

        return new SeedReport(data.Compatibility.Count, data.Bibliography.Count,
            data.Suppliers.Count, data.Catalog.Count, data.Chunks.Count);
    }
}
