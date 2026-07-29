using System.Net.Http;
using Smx.SearchProxy.Config;
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

    /// PROXY_TIMEOUT_SECONDS used to be read by nothing: ProxyOptions parsed it, and no line of code ever
    /// touched the value. The client kept HttpClient's 100-second default, so a hung provider call burned
    /// 100s per attempt and, across the retry loop, up to ~300s — past the platform's ~230s HTTP ceiling,
    /// with the Discovery agent blocked the whole time. Same defect class as the hardcoded `> 20` that
    /// PROXY_MAX_RESULTS used to hit (StructuralGuardTests).
    ///
    /// Lives on ProxyHttp, next to the handler, for the reason stated at the top of that file: the thing
    /// under test must be the thing that ships. HostWiringTests then proves Program.cs actually calls it —
    /// a correct configurator nobody invokes is still a dead knob.
    [Fact]
    public void TheShippedClient_UsesTheConfiguredTimeout_NotHttpClientsDefault()
    {
        using var client = new HttpClient();
        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);   // the default we must not keep

        ProxyHttp.ConfigureClient(client, new ProxyOptions { TimeoutSeconds = 7 });

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    /// A zero or negative timeout is not a timeout — HttpClient throws on it, which would turn a typo in an
    /// app setting into another host-startup crash loop. Fall back to the shipped default instead.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveTimeout_FallsBackToTheDefault_RatherThanThrowing(int configured)
    {
        using var client = new HttpClient();

        ProxyHttp.ConfigureClient(client, new ProxyOptions { TimeoutSeconds = configured });

        Assert.Equal(TimeSpan.FromSeconds(new ProxyOptions().TimeoutSeconds), client.Timeout);
    }
}
