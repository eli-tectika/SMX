using System.Text;
using Smx.Domain;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class InMemoryAttachmentBlobStoreTests
{
    [Fact]
    public async Task RoundTripsText()
    {
        var store = new InMemoryAttachmentBlobStore();
        await store.PutTextAsync("intake/s/f/extracted.txt", "hello");
        Assert.Equal("hello", await store.GetTextAsync("intake/s/f/extracted.txt"));
    }

    [Fact]
    public async Task RoundTripsBytes()
    {
        var store = new InMemoryAttachmentBlobStore();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"));
        await store.PutAsync("intake/s/f/report.pdf", content, "application/pdf");
        Assert.True(store.Exists("intake/s/f/report.pdf"));
    }

    [Fact]
    public async Task Returns_NullForAMissingBlob() =>
        Assert.Null(await new InMemoryAttachmentBlobStore().GetTextAsync("intake/nope/nope/extracted.txt"));
}
