using System.Collections.Concurrent;
using System.Text;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryAttachmentBlobStore : IAttachmentBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public bool Exists(string path) => _blobs.ContainsKey(path);
    public IReadOnlyCollection<string> Paths => _blobs.Keys.ToList();

    public async Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _blobs[path] = ms.ToArray();
    }

    public Task PutTextAsync(string path, string text, CancellationToken ct = default)
    {
        _blobs[path] = Encoding.UTF8.GetBytes(text);
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null);
}
