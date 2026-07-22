using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory chunk source, keyed by document id. A document absent from `Chunks` returns an empty
/// list — which is a real, meaningful state (in bronze, never indexed), not a failure.
public sealed class InMemoryDocumentTextReader : IDocumentTextReader
{
    public Dictionary<string, List<DocumentChunk>> Chunks { get; } = [];

    public Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocumentChunk>>(
            Chunks.TryGetValue(document.Summary.Id, out var c) ? c.ToList() : []);
}
