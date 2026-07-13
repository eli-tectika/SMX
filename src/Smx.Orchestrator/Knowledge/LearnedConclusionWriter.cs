using Microsoft.Extensions.Logging;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Knowledge;

public interface ILearnedConclusionWriter
{
    Task WriteAsync(LearnedConclusionDoc doc, CancellationToken ct);
}

/// The one seam that makes a Learned Conclusion real: authoritative in Cosmos, retrievable via AI Search.
/// Every write path (revise-with-reason; project-close in Plan 5) goes through this, so there is exactly
/// one place where "written" and "findable" can drift apart — and it is this one.
public sealed class LearnedConclusionWriter(
    IKnowledgeStore knowledge,
    ILearnedConclusionsIndex index,
    IEmbedder embedder,
    ILogger<LearnedConclusionWriter> logger) : ILearnedConclusionWriter
{
    public async Task WriteAsync(LearnedConclusionDoc doc, CancellationToken ct)
    {
        // Cosmos FIRST. It is the authoritative copy; the index is a projection of it. If the push below
        // fails, the conclusion still exists and can be re-pushed. The reverse — indexed but not stored —
        // would leave an agent citing a conclusion with no record behind it.
        await knowledge.UpsertLearnedConclusionAsync(doc, ct);

        // Embed the PROJECTED CONTENT — exactly the string the reader matches an agent's question against.
        // Embedding anything narrower (the bare finding, say) puts the write and read vectors in subtly
        // different spaces and degrades every retrieval with no error to show for it.
        var content = LearnedConclusionProjection.Content(doc);
        var vectors = await embedder.EmbedAsync([content], ct);

        // An unembedded chunk is worse than no chunk: the index would hold a document the hybrid reader's
        // vector leg can never match, so the conclusion reads as "written and indexed" while being
        // unfindable. Fail loudly — a re-push can fix a missing document, nothing fixes a silent lie.
        if (vectors.Count == 0)
            throw new InvalidOperationException(
                $"embedder returned no vector for learned conclusion {doc.Id}; refusing to index it unembedded");

        // Idempotent and internally latched, and the ONLY thing that ever creates this index (there is no
        // Bicep resource for it) — so it must run before the first push and is free on every push after.
        await index.EnsureIndexAsync(ct);
        await index.PushAsync([LearnedConclusionProjection.ToChunk(doc, vectors[0])], ct);

        logger.LogInformation("learned conclusion {ConclusionId} written to Cosmos and indexed", doc.Id);
    }
}
