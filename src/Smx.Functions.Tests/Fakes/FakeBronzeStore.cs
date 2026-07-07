using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;

public sealed class FakeBronzeStore : IBronzeStore
{
    public readonly ConcurrentDictionary<string, byte[]> Blobs = new();
    public Task<string> PutAsync(string path, byte[] content, CancellationToken ct) { Blobs[path] = content; return Task.FromResult(path); }
    public Task<byte[]?> GetAsync(string path, CancellationToken ct) => Task.FromResult(Blobs.TryGetValue(path, out var b) ? b : null);
}
