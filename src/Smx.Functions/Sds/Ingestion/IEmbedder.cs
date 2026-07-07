namespace Smx.Functions.Sds.Ingestion;

public interface IEmbedder { Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct); }
