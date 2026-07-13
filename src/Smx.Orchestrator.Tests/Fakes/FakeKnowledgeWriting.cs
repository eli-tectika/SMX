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
