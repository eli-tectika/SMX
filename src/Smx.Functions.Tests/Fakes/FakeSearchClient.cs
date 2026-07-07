using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;

public sealed class FakeSearchClient : ISdsSearchClient
{
    public int EnsureCalls; public readonly List<SdsChunk> Pushed = new();
    public Task EnsureIndexAsync(CancellationToken ct) { EnsureCalls++; return Task.CompletedTask; }
    public Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct) { Pushed.AddRange(chunks); return Task.CompletedTask; }
}
