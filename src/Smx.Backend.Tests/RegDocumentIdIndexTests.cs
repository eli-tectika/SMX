using Smx.Domain.Documents;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The lookup that decides whether a regulatory citation chip can open anything.
///
/// Everything here is about ONE hazard: a well-formed id that resolves to nothing, or to the wrong
/// document. `reg` and `seed` ids are built from the same two segments and differ only in kind, and
/// RegDocumentProvider.GetAsync refuses the mismatched kind — so a guessed kind is a 404 with a plausible
/// URL, which is the "chip opens the wrong thing" failure the whole design exists to prevent.
public class RegDocumentIdIndexTests
{
    private readonly InMemoryRegDocumentSource _source = new();

    private static RegSourceRow Curated(string sourceId, params string[] docIds) =>
        new(sourceId, "REACH", "ECHA", docIds.Select(d => new RegDocTitleRow(d, $"https://x.test/{d}", d)).ToList());

    private static RegDocRow Doc(string sourceId, string docId) =>
        new(docId, sourceId, "sha", "2026-01-01", "sync-202607", "2026-07-01T00:00:00Z");

    [Fact]
    public async Task CuratedSource_MintsARegId()
    {
        _source.Sources.Add(Curated("eur-lex", "reach-annex-xvii"));
        _source.Docs.Add(Doc("eur-lex", "reach-annex-xvii"));

        var id = await new RegDocumentIdIndex(_source).LookupAsync("eur-lex", "reach-annex-xvii");

        Assert.Equal(DocumentId.Encode(DocumentId.Reg, "eur-lex/reach-annex-xvii"), id);
    }

    /// The same two segments, the other kind. A source that is NOT in reg-registry is a seed import, whose
    /// bronze layout has no fetch-timestamp folder — mint `reg` for it and the viewer builds a path that
    /// does not exist.
    [Fact]
    public async Task UncuratedSource_MintsASeedId()
    {
        _source.Docs.Add(Doc("eu", "svhc-list"));

        var id = await new RegDocumentIdIndex(_source).LookupAsync("eu", "svhc-list");

        Assert.Equal(DocumentId.Encode(DocumentId.Seed, "eu/svhc-list"), id);
        Assert.NotEqual(DocumentId.Encode(DocumentId.Reg, "eu/svhc-list"), id);
    }

    /// A chunk whose document the catalog does not list gets NOTHING. This is the case that used to be
    /// tempting to paper over: the index and reg-state can drift, and an id assembled from index fields
    /// alone would look perfectly well-formed and 404.
    [Fact]
    public async Task UnknownPair_YieldsNoId()
    {
        _source.Sources.Add(Curated("eur-lex", "reach-annex-xvii"));
        _source.Docs.Add(Doc("eur-lex", "reach-annex-xvii"));
        var index = new RegDocumentIdIndex(_source);

        Assert.Null(await index.LookupAsync("eur-lex", "some-doc-the-index-still-has"));
        Assert.Null(await index.LookupAsync("no-such-source", "reach-annex-xvii"));
        Assert.Null(await index.LookupAsync(null, "reach-annex-xvii"));
        Assert.Null(await index.LookupAsync("eur-lex", ""));
    }

    /// A row whose own coordinates cannot survive DocumentId.TryDecode — a traversal sequence, a space,
    /// an empty segment. Encode would happily mint one anyway; CanEncode is what stops a link that breaks
    /// on the first click.
    [Theory]
    [InlineData("eur-lex", "../../etc/passwd")]
    [InlineData("eur lex", "reach-annex-xvii")]
    [InlineData("eur-lex", "")]
    public async Task MalformedCoordinates_YieldNoId(string sourceId, string docId)
    {
        _source.Docs.Add(Doc(sourceId, docId));

        Assert.Null(await new RegDocumentIdIndex(_source).LookupAsync(sourceId, docId));
    }

    /// THE DRIFT GUARD. This class deliberately does not call RegDocumentProvider (it would drag in a
    /// content store it never uses — see its own comment), so the curated⇒reg / otherwise⇒seed rule is
    /// stated in two places. This asserts they agree: every id the LIBRARY publishes is the id a CHIP
    /// gets for the same document. If someone changes the rule in one place, this fails.
    [Fact]
    public async Task AgreesWithTheIdsTheCatalogPublishes()
    {
        _source.Sources.Add(Curated("eur-lex", "reach-annex-xvii", "rohs-annex-ii"));
        _source.Docs.Add(Doc("eur-lex", "reach-annex-xvii"));
        _source.Docs.Add(Doc("eur-lex", "rohs-annex-ii"));
        _source.Docs.Add(Doc("us", "prop-65"));
        _source.Docs.Add(Doc("eu", "svhc-list"));

        var published = await new RegDocumentProvider(_source, new InMemoryDocumentContentStore()).ListAsync();
        var index = new RegDocumentIdIndex(_source);

        Assert.Equal(4, published.Count);
        foreach (var row in published)
        {
            Assert.True(DocumentId.TryDecode(row.Id, out var kind, out var payload));
            var segments = DocumentId.SegmentsOf(kind, payload);
            Assert.Equal(row.Id, await index.LookupAsync(segments[0], segments[1]));
        }
    }

    /// An agent calls search_regulatory many times a stage and every hit asks for an id. Two Cosmos queries
    /// per CHUNK is the thing the cache exists to prevent.
    [Fact]
    public async Task ReadsTheRegistryOncePerTtlWindow()
    {
        var clock = new SettableClock();
        var counting = new CountingRegDocumentSource(_source);
        _source.Docs.Add(Doc("eu", "svhc-list"));
        var index = new RegDocumentIdIndex(counting, clock, TimeSpan.FromMinutes(10));

        await index.LookupAsync("eu", "svhc-list");
        await index.LookupAsync("eu", "svhc-list");
        Assert.Equal(1, counting.Builds);

        clock.Advance(TimeSpan.FromMinutes(11));
        await index.LookupAsync("eu", "svhc-list");
        Assert.Equal(2, counting.Builds);
    }

    /// reg-registry and reg-state belong to the regsync Functions app's estate, and a deployment really can
    /// be missing a container. An inert chip is a fine outcome; a Regulatory stage that cannot search is not.
    [Fact]
    public async Task AnUnreadableRegistry_YieldsNoIdRatherThanThrowing()
    {
        var index = new RegDocumentIdIndex(new ThrowingRegDocumentSource());

        Assert.Null(await index.LookupAsync("eur-lex", "reach-annex-xvii"));
    }

    /// Local rather than Microsoft.Extensions.TimeProvider.Testing's FakeTimeProvider — one overridable
    /// method is not worth a package reference the solution does not otherwise carry.
    private sealed class SettableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingRegDocumentSource(IRegDocumentSource inner) : IRegDocumentSource
    {
        public int Builds { get; private set; }

        public Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default)
        {
            Builds++;                       // the first of the two calls one build makes
            return inner.ListSourcesAsync(ct);
        }

        public Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default) => inner.ListDocsAsync(ct);
        public Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default) =>
            inner.GetDocAsync(docId, sourceId, ct);
    }

    private sealed class ThrowingRegDocumentSource : IRegDocumentSource
    {
        public Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("reg-registry container does not exist");
        public Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("reg-state container does not exist");
        public Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default) =>
            throw new InvalidOperationException("reg-state container does not exist");
    }
}
