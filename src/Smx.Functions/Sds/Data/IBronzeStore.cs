namespace Smx.Functions.Sds.Data;

public interface IBronzeStore
{
    Task<string> PutAsync(string path, byte[] content, CancellationToken ct); // returns the stored path
    Task<byte[]?> GetAsync(string path, CancellationToken ct);
}
