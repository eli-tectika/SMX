using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// ICoaDocumentSource over a local directory, selected by BRONZE_LOCAL_PATH — the same local-dev
/// escape hatch LocalBronzeDocumentStore provides, and registered by the same condition.
///
/// It exists so that local dev does not silently report "no certificates". An unimplemented source
/// returning an empty list is indistinguishable from a container with nothing in it, and the whole
/// point of catalogue surfaces here is that absence must not read as coverage.
public sealed class LocalCoaDocumentSource(string root) : ICoaDocumentSource
{
    private string Dir => Path.Combine(Path.GetFullPath(root), CoaDocumentProvider.Prefix);

    public Task<IReadOnlyList<CoaRow>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Dir)) return Task.FromResult<IReadOnlyList<CoaRow>>([]);
        IReadOnlyList<CoaRow> rows = new DirectoryInfo(Dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Select(Row)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<CoaRow?> GetAsync(string fileName, CancellationToken ct = default)
    {
        // Same containment reasoning as LocalBronzeDocumentStore: DocumentId already refuses
        // traversal, and this source still must not be the component that trusts its caller.
        var leaf = Path.GetFileName(fileName);
        if (leaf.Length == 0 || leaf != fileName) return Task.FromResult<CoaRow?>(null);

        var file = new FileInfo(Path.Combine(Dir, leaf));
        return Task.FromResult(file.Exists ? Row(file) : null);
    }

    private static CoaRow Row(FileInfo f) => new(
        f.Name, $"{CoaDocumentProvider.Prefix}/{f.Name}", f.Length, f.LastWriteTimeUtc.ToString("O"));
}
