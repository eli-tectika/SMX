using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IDocumentContentStore over a local directory, selected by BRONZE_LOCAL_PATH. Mirrors the repo's
/// existing *_DRY_RUN convention so the viewer is runnable without Azure.
///
/// The root containment check is deliberate duplication: DocumentId already refuses traversal, but
/// this store accepts a raw string and must not be the single component that trusts its caller.
public sealed class LocalBronzeDocumentStore(string root) : IDocumentContentStore
{
    private readonly string _root = Path.GetFullPath(root);

    public Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        if (full is null || !File.Exists(full)) return Task.FromResult<DocumentBytes?>(null);
        var info = new FileInfo(full);
        Stream stream = File.OpenRead(full);
        return Task.FromResult<DocumentBytes?>(new DocumentBytes(stream, info.Length));
    }

    public async Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        if (full is null || !File.Exists(full)) return null;
        return await File.ReadAllBytesAsync(full, ct);
    }

    private string? Resolve(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || Path.IsPathRooted(blobPath)) return null;
        var combined = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        // Ordinal, with the separator appended, so "/bronze-evil" cannot pass as inside "/bronze".
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal) ? combined : null;
    }
}
