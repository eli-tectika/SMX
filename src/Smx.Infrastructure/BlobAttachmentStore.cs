using System.Text;
using Azure;
using Azure.Storage.Files.DataLake;
using Smx.Domain;

namespace Smx.Infrastructure;

/// The `bronze` ADLS Gen2 filesystem. DataLakeFileSystemClient rather than BlobContainerClient to match
/// AdlsBronzeStore in Smx.Functions, which already writes the SDS corpus into the same filesystem.
public sealed class BlobAttachmentStore(DataLakeFileSystemClient fs) : IAttachmentBlobStore
{
    public async Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default) =>
        await fs.GetFileClient(path).UploadAsync(content, overwrite: true, ct);

    public async Task PutTextAsync(string path, string text, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await fs.GetFileClient(path).UploadAsync(ms, overwrite: true, ct);
    }

    public async Task<string?> GetTextAsync(string path, CancellationToken ct = default)
    {
        var file = fs.GetFileClient(path);
        try
        {
            var resp = await file.ReadAsync(cancellationToken: ct);
            using var reader = new StreamReader(resp.Value.Content, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // A missing blob is a null, not a fault: the session may predate extraction, or the file
            // may have been `unsupported` and never produced text at all.
            return null;
        }
    }
}
