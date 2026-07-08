// src/Smx.Functions/Reference/Ingestion/IReferenceSearchClient.cs
using Smx.Functions.Reference.Domain;

namespace Smx.Functions.Reference.Ingestion;

public interface IReferenceSearchClient
{
    Task EnsureIndexAsync(CancellationToken ct);
    Task PushAsync(IReadOnlyList<ReferenceChunk> chunks, CancellationToken ct);
}
