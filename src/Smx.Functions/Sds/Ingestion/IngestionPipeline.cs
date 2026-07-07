using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class IngestionPipeline
{
    private readonly IBronzeStore _bronze;
    private readonly SdsValidator _validator;
    private readonly IPdfTextExtractor _extractor;
    private readonly GhsChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly ISdsSearchClient _search;
    private readonly RegistryRepo _registry;
    private readonly IReadOnlySet<string> _allowlistDomains;
    private readonly SdsOptions _opts;

    public IngestionPipeline(IBronzeStore bronze, SdsValidator validator, IPdfTextExtractor extractor,
        GhsChunker chunker, IEmbedder embedder, ISdsSearchClient search, RegistryRepo registry,
        IReadOnlySet<string> allowlistDomains, SdsOptions opts)
    { _bronze = bronze; _validator = validator; _extractor = extractor; _chunker = chunker;
      _embedder = embedder; _search = search; _registry = registry; _allowlistDomains = allowlistDomains; _opts = opts; }

    public async Task<IngestResult> IngestAsync(byte[] pdf, SdsMetadata meta, string sourceDomain, CancellationToken ct)
    {
        var blobPath = $"sds/{meta.Cas}/{meta.Supplier}/{meta.RevisionDate}.pdf";
        await _bronze.PutAsync(blobPath, pdf, ct);

        var text = _extractor.Extract(pdf);
        var validation = _validator.Validate(text, meta.Cas, sourceDomain, _allowlistDomains);
        if (!validation.Ok) return new IngestResult(false, validation.Reason, null);

        var sections = _chunker.Chunk(text);
        var vectors = await _embedder.EmbedAsync(sections.Select(s => s.Content).ToList(), ct);

        var registryId = DedupKey.ForRegistry(meta.Cas, meta.Supplier, meta.RevisionDate);
        var chunks = new List<SdsChunk>(sections.Count);
        for (var i = 0; i < sections.Count; i++)
            chunks.Add(new SdsChunk($"{registryId}#{i}", meta.Cas, meta.Supplier, meta.ProductName,
                meta.RevisionDate, meta.Region, meta.Language, sections[i].Section, sections[i].Content,
                vectors[i], blobPath, meta.MasterListId));

        await _search.EnsureIndexAsync(ct);
        await _search.PushAsync(chunks, ct);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await _registry.UpsertAsync(new RegistryPointer(registryId, meta.Cas, meta.Supplier, meta.ProductName,
            meta.RevisionDate, meta.Region, meta.Language, meta.SourceUrl, blobPath, true,
            chunks.Select(c => c.Id).ToList(), now, null, meta.MasterListId), ct);

        return new IngestResult(true, null, registryId);
    }
}
