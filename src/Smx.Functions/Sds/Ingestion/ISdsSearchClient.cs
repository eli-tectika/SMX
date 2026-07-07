using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public interface ISdsSearchClient
{
    Task EnsureIndexAsync(CancellationToken ct);
    Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct);
}
