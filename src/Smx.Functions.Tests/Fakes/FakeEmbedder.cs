using Smx.Functions.Sds.Ingestion;

public sealed class FakeEmbedder : IEmbedder
{
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[3072]).ToList());
}
