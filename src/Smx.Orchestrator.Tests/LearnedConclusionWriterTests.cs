using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// LearnedConclusionWriter is the one seam that makes a Learned Conclusion real: authoritative in Cosmos,
/// retrievable via AI Search. Every write path (revise-with-reason; project-close in Plan 5) goes through
/// it, so there is exactly one place where "written" and "findable" can drift apart — and it is this one.
public class LearnedConclusionWriterTests
{
    private const string ScopeKey = "proj-1|revision|discovery|aaaa1111";

    private static LearnedConclusionDoc Doc() => new()
    {
        Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, ScopeKey),
        Kind = KnowledgeKinds.Material,
        Scope = new("Ba", null, "HDPE", null, null, null),
        Finding = "Barium sulfate is unsuitable for XRF-marked HDPE where Ti is present.",
        Confidence = 0.7,
        Provenance = new(["proj-1"], ["revision … — operator reason: overlaps the Ti K-beta line"]),
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    private static LearnedConclusionWriter Writer(
        IKnowledgeStore knowledge, ILearnedConclusionsIndex index, IEmbedder embedder) =>
        new(knowledge, index, embedder, NullLogger<LearnedConclusionWriter>.Instance);

    [Fact]
    public async Task Write_LandsInCosmos_AndInTheIndex_WithTheEmbeddedContent()
    {
        var knowledge = new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        var embedder = new FakeEmbedder();
        var writer = Writer(knowledge, index, embedder);

        await writer.WriteAsync(Doc(), default);

        // Authoritative copy.
        Assert.NotNull(await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, ScopeKey));

        // Retrievable copy — the index is created before the push (it does not exist until now).
        Assert.Equal(1, index.EnsureCalls);
        var chunk = Assert.Single(index.Pushed);
        Assert.Equal(LearnedConclusionProjection.Content(Doc()), chunk.Content);
        Assert.Equal(3072, chunk.ContentVector.Length);

        // The vector must be of the PROJECTED CONTENT, not of the bare finding: the reader matches an
        // agent's question against the whole content string, so embedding anything else misaligns the two
        // vector spaces and quietly degrades every retrieval with nothing to show for it.
        Assert.Equal([LearnedConclusionProjection.Content(Doc())], embedder.Embedded);
    }

    [Fact]
    public async Task Write_PersistsToCosmosEvenIfTheIndexPushFails()
    {
        // Cosmos is authoritative; the index is a projection. A conclusion that exists but is not yet
        // retrievable can be re-pushed later — an indexed conclusion with no Cosmos record would be a
        // citation pointing at nothing. So Cosmos must be written FIRST and must survive a push failure.
        var knowledge = new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore();
        var writer = Writer(knowledge, new ThrowingIndex(), new FakeEmbedder());

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Doc(), default));

        Assert.NotNull(await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, ScopeKey));
    }

    [Fact]
    public async Task Write_PushesTheSearchSafeKey_DeterministicallyAndAzureLegal()
    {
        // The chunk's key is LearnedConclusionProjection.SearchKey(doc.Id), NOT the raw Cosmos id. That id is
        // pipe-delimited ("material|proj-1|revision|discovery|aaaa1111") and Azure AI Search rejects any key
        // outside [A-Za-z0-9_\-=] — silently, since nothing inspects IndexDocumentsResult. Lose the mapping in
        // a refactor of ToChunk and every push is dropped: the index stays empty and search_learned_conclusions
        // answers "no prior conclusions" forever, with every other test here still green.
        //
        // And it must be DETERMINISTIC: the change feed is at-least-once, so re-delivering the same revision
        // must upsert the same document rather than accumulate duplicates — the exact property
        // KnowledgeIds.RevisionConclusion was designed for, which dies if the key is not stable across writes.
        var index = new FakeLearnedConclusionsIndex();
        var writer = Writer(new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), index, new FakeEmbedder());

        await writer.WriteAsync(Doc(), default);
        await writer.WriteAsync(Doc(), default);   // change-feed redelivery of the same revision

        Assert.Equal(2, index.Pushed.Count);
        var key = index.Pushed[0].Id;
        Assert.Equal(LearnedConclusionProjection.SearchKey(Doc().Id), key);
        Assert.Equal(key, index.Pushed[1].Id);                 // stable → an upsert, not a duplicate
        Assert.DoesNotContain('|', key);
        Assert.Matches("^[A-Za-z0-9_=-]+$", key);              // Azure's document-key alphabet
    }

    [Fact]
    public async Task Write_EmbedderReturnsNothing_FailsLoudly_RatherThanIndexingAnUnembeddedConclusion()
    {
        // A vectorless chunk is worse than no chunk: the index would hold a document the hybrid reader's
        // vector leg can never match, so the conclusion is "written and indexed" and still unfindable. Crash.
        var writer = Writer(
            new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new EmptyEmbedder());

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Doc(), default));
        Assert.Contains(Doc().Id, e.Message);
    }

    private sealed class ThrowingIndex : ILearnedConclusionsIndex
    {
        public Task EnsureIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default) =>
            throw new InvalidOperationException("search unavailable");
    }

    private sealed class EmptyEmbedder : IEmbedder
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([]);
    }
}
