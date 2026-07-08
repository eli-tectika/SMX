// src/Smx.Functions.Tests/Fakes/FakeReferenceSearchClient.cs
using Smx.Functions.Reference.Domain;
using Smx.Functions.Reference.Ingestion;

public sealed class FakeReferenceSearchClient : IReferenceSearchClient
{
    public int EnsureCalls;
    public readonly List<ReferenceChunk> Pushed = new();
    public Task EnsureIndexAsync(CancellationToken ct) { EnsureCalls++; return Task.CompletedTask; }
    public Task PushAsync(IReadOnlyList<ReferenceChunk> chunks, CancellationToken ct)
    { Pushed.AddRange(chunks); return Task.CompletedTask; }
}
