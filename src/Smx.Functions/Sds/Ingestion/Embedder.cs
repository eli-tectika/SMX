using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace Smx.Functions.Sds.Ingestion;

public sealed class Embedder : IEmbedder
{
    private readonly EmbeddingClient _client;
    public Embedder(AzureOpenAIClient client, string deployment) => _client = client.GetEmbeddingClient(deployment);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var resp = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return resp.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
