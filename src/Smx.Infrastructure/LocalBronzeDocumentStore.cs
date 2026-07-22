using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IDocumentContentStore over a local directory, selected by BRONZE_LOCAL_PATH. Mirrors the repo's
/// existing *_DRY_RUN convention so the viewer is runnable without Azure.
///
/// The root containment check is deliberate duplication: DocumentId already refuses traversal, but
/// this store accepts a raw string and must not be the single component that trusts its caller.
///
/// Containment is LEXICAL, and that boundary is worth stating rather than leaving to be discovered.
/// `Path.GetFullPath` normalises `.`, `..` and relative prefixes without touching the filesystem, so
/// it does not follow symlinks: a link planted inside the root and pointing outside it would satisfy
/// the prefix check and then read elsewhere. Left unhandled deliberately — this store exists only for
/// local dev (`BRONZE_LOCAL_PATH`), production reads ADLS through BronzeDocumentStore where the
/// filesystem is remote and this class of trick does not apply, and planting the link already
/// requires write access to the developer's own disk. If this ever becomes a production path, the
/// fix is `FileSystemInfo.ResolveLinkTarget` plus re-validating the resolved path.
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

    /// Resolved through the same containment check as the reads: "does /etc/passwd exist" is itself a
    /// fact about the host, and an out-of-root path gets the same answer as an absent one.
    public Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        return Task.FromResult(full is not null && File.Exists(full));
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
