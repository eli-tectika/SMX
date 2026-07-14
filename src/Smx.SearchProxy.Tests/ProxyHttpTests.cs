using System.Net.Http;
using Smx.SearchProxy.Providers;
using Xunit;

namespace Smx.SearchProxy.Tests;

/// The anonymity half of this handler is tested in TracePropagationTests, against a real socket. What is
/// left here is the connection-pool half, which no other test can see.
public class ProxyHttpTests
{
    /// SearchPipeline is a SINGLETON and captures the typed client, so the handler below is built once and
    /// lives as long as the Flex Consumption instance does — which, on an always-ready instance, is
    /// indefinitely. With the default PooledConnectionLifetime (infinite) the TCP connection to
    /// api.search.brave.com is never recycled, so DNS is resolved exactly once, at startup, and a provider
    /// IP change is never picked up: every search fails against a stale address until someone restarts the
    /// app. The agent then reads "the external search is unavailable" indefinitely.
    ///
    /// The invariant is that the lifetime is FINITE — the exact value is a tuning choice, so assert the
    /// property that matters rather than restating the constant.
    [Fact]
    public void TheShippedHandler_RecyclesPooledConnections_SoDnsIsReResolved()
    {
        using var handler = ProxyHttp.CreateHandler();

        Assert.NotEqual(Timeout.InfiniteTimeSpan, handler.PooledConnectionLifetime);
        Assert.True(handler.PooledConnectionLifetime > TimeSpan.Zero);
        Assert.True(handler.PooledConnectionLifetime <= TimeSpan.FromMinutes(15));
    }
}
