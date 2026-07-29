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
        var client = new NatEgressClient(new HttpClient(new TarpitHandler()), opts,
            NullLogger<NatEgressClient>.Instance);

        var fetch = client.FetchAsync(new Uri("https://tarpit.example/1310-73-2.pdf"), default);
        var winner = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(fetch, winner);           // fetch must complete on its own, not hang
        Assert.Null(await fetch);             // and report a miss, not throw
    }

    // ---- what the allowlist gate was replaced by ----

    private sealed class OkHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent("%PDF-1.4 pretend sheet"u8.ToArray()),
            });
        }
    }

    private static NatEgressClient Client(OkHandler handler, params string[] denylist)
        => new(new HttpClient(handler), new SdsOptions { Denylist = denylist }, NullLogger<NatEgressClient>.Instance);

    // THE change: a host nobody curated is now fetched. Coverage used to equal the size of a
    // hand-maintained dictionary; correctness is carried by SdsValidator reading the document instead.
    [Fact]
    public async Task An_uncurated_host_is_fetched()
    {
        var handler = new OkHandler();
        Assert.NotNull(await Client(handler).FetchAsync(new Uri("https://never-curated.example/x.pdf"), default));
        Assert.Equal(1, handler.Calls);
    }

    // Rails that survive, because they are robustness rather than policy. Each asserts the request was
    // never made — refusing after egress would defeat the point.
    [Fact]
    public async Task Plain_http_is_refused()
    {
        var handler = new OkHandler();
        Assert.Null(await Client(handler).FetchAsync(new Uri("http://example.com/x.pdf"), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_denylisted_host_is_refused()
    {
        var handler = new OkHandler();
        Assert.Null(await Client(handler, "tarpit.example")
            .FetchAsync(new Uri("https://tarpit.example/x.pdf"), default));
        Assert.Equal(0, handler.Calls);
    }

    // A denylist entry covers its subdomains, or evading it would be a matter of prefixing a label.
    [Fact]
    public async Task A_denylisted_hosts_subdomains_are_refused_too()
    {
        var handler = new OkHandler();
        Assert.Null(await Client(handler, "tarpit.example")
            .FetchAsync(new Uri("https://cdn.tarpit.example/x.pdf"), default));
        Assert.Equal(0, handler.Calls);
    }

    // ...but it must not swallow a host that merely ENDS with the same letters.
    [Fact]
    public async Task A_lookalike_host_is_not_denylisted()
    {
        var handler = new OkHandler();
        Assert.NotNull(await Client(handler, "tarpit.example")
            .FetchAsync(new Uri("https://nottarpit.example/x.pdf"), default));
    }
}
