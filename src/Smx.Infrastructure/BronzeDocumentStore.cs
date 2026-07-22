using Azure;
using Azure.Storage.Files.DataLake;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IDocumentContentStore over the ADLS Gen2 `bronze` filesystem, read-only.
///
/// The backend's UAMI already holds Storage Blob Data Contributor at account scope
/// (infra/modules/data.bicep) — this class is the code that was missing, not the permission.
/// It reads and never writes: the interface has no Put, and this feature adds no state.
///
/// Only a 404 is swallowed to null. A 403 (RBAC not yet propagated) or a network failure is left to
/// propagate as an exception rather than be folded into "no such document" — the spec requires a
/// document that cannot be shown to say why, and a permissions problem silently reading as "missing"
/// would defeat that.
public sealed class BronzeDocumentStore(DataLakeFileSystemClient filesystem) : IDocumentContentStore
{
    public async Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        var file = filesystem.GetFileClient(blobPath);
        try
        {
            // FileDownloadInfo (the ReadAsync response) already carries ContentLength — a separate
            // GetPropertiesAsync call would just be a second round trip for a value this one already has.
            var response = await file.ReadAsync(ct);
            return new DocumentBytes(response.Value.Content, response.Value.ContentLength);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    /// A metadata-only round trip (GetProperties under the hood), not a download. The SDK folds a 404
    /// into `false` itself and lets a 403 or a transport failure throw, which is the same split the
    /// read methods make deliberately above.
    public async Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
        => await filesystem.GetFileClient(blobPath).ExistsAsync(ct);

    public async Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        var file = filesystem.GetFileClient(blobPath);
        try
        {
            var response = await file.ReadAsync(ct);
            using var buffer = new MemoryStream();
            await response.Value.Content.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }
}
