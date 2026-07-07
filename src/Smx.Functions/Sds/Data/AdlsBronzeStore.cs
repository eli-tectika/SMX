using Azure.Storage.Files.DataLake;

namespace Smx.Functions.Sds.Data;

public sealed class AdlsBronzeStore : IBronzeStore
{
    private readonly DataLakeFileSystemClient _fs;
    public AdlsBronzeStore(DataLakeFileSystemClient fs) => _fs = fs;

    public async Task<string> PutAsync(string path, byte[] content, CancellationToken ct)
    {
        var file = _fs.GetFileClient(path);
        using var ms = new MemoryStream(content);
        await file.UploadAsync(ms, overwrite: true, ct);
        return path;
    }

    public async Task<byte[]?> GetAsync(string path, CancellationToken ct)
    {
        var file = _fs.GetFileClient(path);
        if (!await file.ExistsAsync(ct)) return null;
        var resp = await file.ReadAsync(cancellationToken: ct);
        using var ms = new MemoryStream();
        await resp.Value.Content.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
