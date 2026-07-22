namespace Smx.Domain.Documents;

/// An open read stream and its length.
///
/// Deliberately NOT IDisposable, despite wrapping a disposable. The only consumer is the content
/// endpoint, which hands the stream to `Results.Stream` — and ASP.NET Core takes ownership there and
/// disposes it after writing the response. Making this disposable would invite a `using` around the
/// call, which would close the stream *before* the framework wrote a single byte.
///
/// The contract is therefore: the caller owns `Stream` until it transfers ownership. Any caller that
/// does not pass it to the framework must dispose it itself.
public sealed record DocumentBytes(Stream Stream, long Length);

/// Read-only access to the bronze filesystem. There is deliberately no write method: this feature
/// writes nothing, and an interface with no Put cannot grow one by accident (spec §3 invariant 7).
public interface IDocumentContentStore
{
    Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default);

    /// Whole-blob read, for the small `meta.json` sidecars.
    Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default);
}
