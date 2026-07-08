using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

public interface IRegSearchClient
{
    Task EnsureIndexAsync(CancellationToken ct);
    Task PushAsync(IReadOnlyList<GoldChunk> chunks, CancellationToken ct);
}
