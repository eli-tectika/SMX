using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// ICoaDocumentSource over the ADLS Gen2 `bronze` filesystem. The container IS the registry — see
/// CoaRow — so this lists a prefix rather than querying Cosmos.
///
/// Like BronzeDocumentStore it swallows only a 404 (the prefix does not exist yet, i.e. no COA has
/// ever been uploaded). A 403 or a network failure propagates: a permissions problem that read as
/// "no certificates" would let an empty library look like a complete one.
public sealed class BronzeCoaDocumentSource(DataLakeFileSystemClient filesystem) : ICoaDocumentSource
{
    public async Task<IReadOnlyList<CoaRow>> ListAsync(CancellationToken ct = default)
    {
        var rows = new List<CoaRow>();
        try
        {
            await foreach (var path in filesystem.GetPathsAsync(
                CoaDocumentProvider.Prefix, recursive: false, cancellationToken: ct))
            {
                if (path.IsDirectory == true || path.Name is null) continue;
                rows.Add(ToRow(path));
            }
        }
        catch (RequestFailedException e) when (e.Status == 404) { return []; }
        return rows;
    }

    public async Task<CoaRow?> GetAsync(string fileName, CancellationToken ct = default)
    {
        // The caller's value has already been through DocumentId.TryDecode, which refuses '/', '\'
        // and "..", so it cannot climb out of the prefix. Re-deriving the leaf here rather than
        // trusting the string wholesale keeps that guarantee local to the code that builds the path.
        var leaf = Path.GetFileName(fileName);
        if (leaf.Length == 0 || leaf != fileName) return null;

        var file = filesystem.GetFileClient($"{CoaDocumentProvider.Prefix}/{leaf}");
        try
        {
            var props = await file.GetPropertiesAsync(cancellationToken: ct);
            return new CoaRow(leaf, $"{CoaDocumentProvider.Prefix}/{leaf}",
                props.Value.ContentLength, props.Value.LastModified.UtcDateTime.ToString("O"));
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    private static CoaRow ToRow(PathItem path) => new(
        FileName: Path.GetFileName(path.Name),
        BlobPath: path.Name,
        SizeBytes: path.ContentLength ?? 0,
        LastModifiedUtc: path.LastModified.UtcDateTime.ToString("O"));
}
