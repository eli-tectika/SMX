using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

/// The write-side twins of the learned-conclusions loop. Shared by LearnedConclusionWriterTests (the write
/// path) and LearnedConclusionsSearchToolTests (the read path) — one fake, so the two sides cannot drift
/// apart on the contract they must agree on.

/// The same embedder contract the production FoundryEmbedder implements: one vector per text. Records what
/// it was asked to embed, so a test can assert the QUERY (read side) or the PROJECTED CONTENT (write side)
/// is what got embedded — not the empty string, not nothing at all.
public sealed class FakeEmbedder : IEmbedder
{
    public List<string> Embedded { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Embedded.AddRange(texts);
        // 3072 = text-embedding-3-large, matching LearnedConclusionsIndex's VectorDims. A wrong length here
        // would pass in-memory and be rejected by the real index.
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[3072]).ToList());
    }
}

public sealed class FakeLearnedConclusionsIndex : ILearnedConclusionsIndex
{
    public int EnsureCalls;
    public List<LearnedConclusionChunk> Pushed { get; } = [];

    public Task EnsureIndexAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref EnsureCalls);
        return Task.CompletedTask;
    }

    public Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default)
    {
        Pushed.AddRange(chunks);
        return Task.CompletedTask;
    }
}

/// An ILearnedConclusionsSearch that can see ONLY what the writer actually pushed, and only through the
/// same keyhole the real reader looks through: `id` and `content`, nothing else.
///
/// It scores by term overlap on the content string rather than doing real BM25/vector search — the point
/// is not to reproduce Azure's ranker, it is to make it IMPOSSIBLE for a round-trip test to pass by
/// reading a field the production reader never selects. If the writer stops putting the operator's reason
/// into `content`, this double stops finding it, exactly as production would.
public sealed class IndexBackedLearnedConclusionsSearch(FakeLearnedConclusionsIndex index) : ILearnedConclusionsSearch
{
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        var terms = query.Split([' ', '?', ',', '.'], StringSplitOptions.RemoveEmptyEntries);
        var hits = index.Pushed
            .Select(c => (chunk: c, score: terms.Count(t => c.Content.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(top)
            .Select(x => new RetrievedChunk("learned-conclusions", $"learned-conclusions/{x.chunk.Id}", x.chunk.Content, x.score))
            .ToList();
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(hits);
    }
}
