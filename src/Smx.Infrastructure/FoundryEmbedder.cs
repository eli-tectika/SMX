using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using Smx.Domain.Tools;

namespace Smx.Infrastructure;

/// text-embedding-3-large on the Foundry account — the same endpoint and the same Entra credential as
/// the chat model, so there is no second resource, no second key and no new RBAC to grant.
///
/// Backs both sides of learned-conclusions retrieval: the vectors pushed into the index and the query
/// vector the search tool sends. One implementation, therefore one model, therefore comparable vectors.
///
/// This duplicates Smx.Functions' Sds/Ingestion/Embedder rather than sharing it: that type lives in the
/// Functions worker assembly (a different solution), which the orchestrator does not — and should not —
/// reference.
public sealed class FoundryEmbedder : IEmbedder
{
    private readonly EmbeddingClient _client;

    public FoundryEmbedder(AzureOpenAIClient client, string deployment) =>
        _client = client.GetEmbeddingClient(deployment);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var response = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
