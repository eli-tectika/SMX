namespace Smx.Domain.Documents;

public sealed record DocumentBytes(Stream Stream, long Length);

/// Read-only access to the bronze filesystem. There is deliberately no write method: this feature
/// writes nothing, and an interface with no Put cannot grow one by accident (spec §3 invariant 7).
public interface IDocumentContentStore
{
    Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default);

    /// Whole-blob read, for the small `meta.json` sidecars.
    Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default);
}
