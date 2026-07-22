using System.Text;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class LocalBronzeDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smx-bronze-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task ReadsAStoredBlob()
    {
        Write("sds/7761-88-8/sigma/2024-03-11.pdf", "%PDF-1.4 hello");
        var store = new LocalBronzeDocumentStore(_root);

        var bytes = await store.ReadAsync("sds/7761-88-8/sigma/2024-03-11.pdf");

        Assert.Equal("%PDF-1.4 hello", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public async Task OpenReportsTheLength()
    {
        Write("regulatory/a/b/ts/raw.html", "<html></html>");
        var store = new LocalBronzeDocumentStore(_root);

        var opened = await store.OpenAsync("regulatory/a/b/ts/raw.html");

        Assert.NotNull(opened);
        Assert.Equal(13, opened!.Length);
        opened.Stream.Dispose();
    }

    [Fact]
    public async Task ReturnsNullForAMissingBlob()
    {
        var store = new LocalBronzeDocumentStore(_root);
        Assert.Null(await store.ReadAsync("nope/missing.pdf"));
        Assert.Null(await store.OpenAsync("nope/missing.pdf"));
    }

    [Fact]
    public async Task ExistsAnswersWithoutOpeningTheFile()
    {
        Write("sds/7761-88-8/sigma/2024-03-11.pdf", "%PDF-1.4 hello");
        var store = new LocalBronzeDocumentStore(_root);

        Assert.True(await store.ExistsAsync("sds/7761-88-8/sigma/2024-03-11.pdf"));
        Assert.False(await store.ExistsAsync("sds/7761-88-8/sigma/1999-01-01.pdf"));
    }

    // Defence in depth. DocumentId already refuses traversal, but this store takes a raw string and
    // must not be the one component that trusts it — the root is a containment boundary, and a
    // future caller may not be DocumentId.
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("sds/../../secrets.txt")]
    [InlineData("/etc/passwd")]
    public async Task RefusesToEscapeTheRoot(string path)
    {
        Write("secrets.txt", "top secret");
        var store = new LocalBronzeDocumentStore(_root);
        Assert.Null(await store.ReadAsync(path));
        Assert.Null(await store.OpenAsync(path));
        // Existence is an answer too: a caller that learns "/etc/passwd is there" has been told
        // something about the host that the containment boundary exists to withhold.
        Assert.False(await store.ExistsAsync(path));
    }
}
