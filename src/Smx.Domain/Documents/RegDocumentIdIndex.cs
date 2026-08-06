namespace Smx.Domain.Documents;

/// Resolves the coordinates a regulatory AI Search chunk carries — `sourceId` + `docId`, the only two
/// document fields on the index (RegSearchClient.EnsureIndexAsync) — to the document id the viewer will
/// actually open, or null.
public interface IRegDocumentIdIndex
{
    Task<string?> LookupAsync(string? sourceId, string? docId, CancellationToken ct = default);
}

/// The bridge from a retrieved regulatory chunk to an openable document.
///
/// It has to exist because a chunk cannot know its own document's KIND, and the kind is half the id.
/// `reg_{b64(sourceId/docId)}` and `seed_{b64(sourceId/docId)}` differ ONLY by whether the sourceId is a
/// curated sync source — a fact that lives in `reg-registry` and was never pushed to the index — and
/// RegDocumentProvider.GetAsync refuses the mismatched kind outright ("a hand-edited id pointing at a path
/// that does not exist"). So a guessed kind is a well-formed id that 404s, which is the "chip opens the
/// wrong thing" failure wearing a new costume. Nothing here guesses: an id is minted only for a document
/// the catalog actually lists, and a chunk we cannot place gets NO id and an inert chip.
///
/// MIRRORS RegDocumentProvider.ListAsync's rule (curated ⇒ `reg`, otherwise ⇒ `seed`) and MUST NOT DRIFT
/// from it — the same register as SdsRegistryKey vs DedupKey. It does not call the provider because the
/// provider takes an IDocumentContentStore it would never use here, and resolving that eagerly would let a
/// deployment with no bronze account break regulatory RETRIEVAL. The guard is RegDocumentIdIndexTests,
/// which runs both over the same source and asserts the ids agree.
public sealed class RegDocumentIdIndex(
    IRegDocumentSource source, TimeProvider? clock = null, TimeSpan? ttl = null) : IRegDocumentIdIndex
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// The corpus changes when the monthly sync or a seed import runs, so a few minutes of staleness costs
    /// nothing: a document added since the last build is simply not linkable yet (inert, the safe reading),
    /// and one removed since keeps a link that 404s until the window closes. Caching matters because an
    /// agent calls search_regulatory many times per stage and this would otherwise put two Cosmos queries
    /// behind every one of them.
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);

    /// A failed build is cached too — briefly. Not caching it would retry two Cosmos queries per CHUNK
    /// against an estate that just told us it is broken; caching it for the full TTL would blind the chips
    /// for ten minutes over one transient error.
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public async Task<string?> LookupAsync(string? sourceId, string? docId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(docId)) return null;
        var map = await MapAsync(ct);
        return map.GetValueOrDefault(Key(sourceId, docId));
    }

    private async Task<Dictionary<string, string>> MapAsync(CancellationToken ct)
    {
        if (_clock.GetUtcNow() < _expires) return _map;
        await _gate.WaitAsync(ct);
        try
        {
            if (_clock.GetUtcNow() < _expires) return _map;   // another caller rebuilt while we waited
            var (map, ok) = await BuildAsync(ct);
            _map = map;
            _expires = _clock.GetUtcNow() + (ok ? _ttl : FailureTtl);
            return _map;
        }
        finally { _gate.Release(); }
    }

    private async Task<(Dictionary<string, string> Map, bool Ok)> BuildAsync(CancellationToken ct)
    {
        IReadOnlyList<RegSourceRow> sources;
        IReadOnlyList<RegDocRow> docs;
        try
        {
            sources = await source.ListSourcesAsync(ct);
            docs = await source.ListDocsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A document-id lookup is DECORATION on a retrieval path that must keep working. reg-registry
            // and reg-state belong to the regsync Functions app's estate, so a deployment can genuinely be
            // missing them (that drift has happened here before) — and letting that take down every
            // regulatory search would trade an inert chip for a dead Regulatory stage.
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false);
        }

        var curated = sources.Select(s => s.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs)
        {
            var kind = curated.Contains(d.SourceId) ? DocumentKinds.Reg : DocumentKinds.Seed;
            var payload = $"{d.SourceId}/{d.DocId}";
            // CanEncode, not Encode: a sourceId or docId with an empty segment, a space or a traversal
            // sequence encodes into something that LOOKS like an id and then fails TryDecode the moment
            // anyone clicks it. No id at all is the honest answer for a row like that.
            if (!DocumentId.CanEncode(kind, payload)) continue;
            map[Key(d.SourceId, d.DocId)] = DocumentId.Encode(kind, payload);
        }
        return (map, true);
    }

    // '\n' cannot occur in either half (DocumentId.TryDecode refuses control characters and whitespace in
    // a reg/seed payload), so it cannot collide two different pairs into one key.
    private static string Key(string sourceId, string docId) => $"{sourceId}\n{docId}";
}
