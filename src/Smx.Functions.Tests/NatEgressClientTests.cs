using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Sourcing;
using Xunit;

// Regression guard for the live 2026-07-16 sweep wedge: with ResponseHeadersRead,
// HttpClient.Timeout stops covering the body read, so a tarpit server that returns headers and
// then trickles the body forever hung the sweep indefinitely (40+ min observed). The fetch
// timeout must bound the WHOLE fetch — headers and body.
public class NatEgressClientTests
{
    // Sends headers immediately, then never completes the body until the read is cancelled.
    private sealed class TarpitHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new TarpitStream())
            });

        private sealed class TarpitStream : Stream
        {
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            { await Task.Delay(Timeout.Infinite, ct); return 0; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            { await Task.Delay(Timeout.Infinite, ct); return 0; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Fetch_returns_null_within_the_fetch_timeout_when_the_body_read_tarpits()
    {
        var opts = new SdsOptions { FetchTimeoutSeconds = 1 };
        var allow = AllowlistProvider.FromJson("""
          [ { "supplier":"S","domain":"tarpit.example","priority":1,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://tarpit.example/{cas}.pdf" } ]
        """);
        var client = new NatEgressClient(new HttpClient(new TarpitHandler()), allow, opts,
            NullLogger<NatEgressClient>.Instance);

        var fetch = client.FetchAsync(new Uri("https://tarpit.example/1310-73-2.pdf"), default);
        var winner = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(fetch, winner);           // fetch must complete on its own, not hang
        Assert.Null(await fetch);             // and report a miss, not throw
    }
}
