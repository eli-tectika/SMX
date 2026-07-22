using System.Text;
using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory bronze. `Blobs` is keyed by blob path.
///
/// `PathsRead` records every path this store was asked for — by ANY method, existence checks
/// included — so tests can assert that a rejected document id never reached storage at all
/// (spec §3 invariant 2); a 404 is not by itself proof that no read was attempted.
///
/// `ContentReads` counts only the calls that actually move bytes, which is a different question:
/// it is how a test tells "asked whether the blob is there" apart from "downloaded the blob".
public sealed class InMemoryDocumentContentStore : IDocumentContentStore
{
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public List<string> PathsRead { get; } = [];
    public int ContentReads { get; private set; }

    public void Put(string path, string content) => Blobs[path] = Encoding.UTF8.GetBytes(content);

    public Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        PathsRead.Add(blobPath);
        ContentReads++;
        if (!Blobs.TryGetValue(blobPath, out var bytes)) return Task.FromResult<DocumentBytes?>(null);
        return Task.FromResult<DocumentBytes?>(new DocumentBytes(new MemoryStream(bytes), bytes.Length));
    }

    public Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        PathsRead.Add(blobPath);
        ContentReads++;
        return Task.FromResult(Blobs.TryGetValue(blobPath, out var bytes) ? bytes : null);
    }

    public Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
    {
        PathsRead.Add(blobPath);
        return Task.FromResult(Blobs.ContainsKey(blobPath));
    }
}
