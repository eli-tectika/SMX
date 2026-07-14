using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Smx.SearchProxy.Providers;
using Xunit;

namespace Smx.SearchProxy.Tests;

/// Invariant: nothing we send to the provider can correlate our requests to each other.
///
/// This suite exists because the OBVIOUS way to test it does not work. A stub HttpMessageHandler (as in
/// BraveSearchProviderTests) sits ABOVE the runtime's DiagnosticsHandler — the component that injects
/// `traceparent` — so it observes the request before the header would ever be added, and its assertion
/// passes whether or not we suppress propagation. To actually test the invariant, the request has to make it
/// all the way through a REAL SocketsHttpHandler and out onto a socket. So we stand up a loopback listener
/// and read the headers off the wire.
///
/// Why this one matters more than ordinary header hygiene: `traceparent` carries a single trace id for the
/// whole cover batch. Handed to the provider, it says "these N queries are one caller's batch" — which
/// collapses the haystack and defeats the k-anonymity outright, while every other test stays green.
public class TracePropagationTests
{
    /// Sends one GET through `handler` from inside a live trace context, and returns the headers the server
    /// actually received.
    private static async Task<NameValueCollection> HeadersSeenByTheServer(HttpMessageHandler handler)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prefix = $"http://127.0.0.1:{FreePort()}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        // Scoped to the whole method: disposing an HttpClient cancels its in-flight requests, so it has to
        // outlive the send we are about to await.
        using var http = new HttpClient(handler);
        try
        {
            var incoming = listener.GetContextAsync();

            // There MUST be an ambient Activity, or this test would pass VACUOUSLY: with no trace context in
            // scope .NET has nothing to inject, and "no traceparent" would prove nothing about our handler.
            // The W3C id format is set on the instance (not via the process-global Activity.DefaultIdFormat)
            // so this test cannot perturb any other test in the run.
            var activity = new Activity("test");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            Task<HttpResponseMessage> send;
            try
            {
                send = http.GetAsync(prefix, cts.Token);
            }
            finally
            {
                activity.Stop();
            }

            var ctx = await incoming.WaitAsync(cts.Token);
            var headers = ctx.Request.Headers;
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();

            using var resp = await send;
            return headers;
        }
        finally
        {
            listener.Stop();
            ((IDisposable)listener).Dispose();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// THE INVARIANT. The handler we actually ship must put no correlation header on the wire.
    [Fact]
    public async Task TheShippedHandler_SendsNoTraceparent()
    {
        var headers = await HeadersSeenByTheServer(ProxyHttp.CreateHandler());

        Assert.Null(headers["traceparent"]);
        Assert.Null(headers["tracestate"]);
        Assert.Null(headers["Request-Id"]);   // the legacy hierarchical format, same leak by another name
        Assert.Null(headers["Correlation-Context"]);
    }

    /// THE CONTROL. Identical request, DEFAULT handler — this one MUST see a traceparent.
    ///
    /// It is the only thing that gives the test above any teeth. If .NET stopped injecting the header (a
    /// runtime change, DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION=0 in the environment, no Activity in
    /// scope), the test above would pass for a reason that has nothing to do with our code, and the day
    /// someone deleted the propagator it would go on passing. This test fails in that world, loudly.
    [Fact]
    public async Task Control_TheDefaultHandlerDoesSendATraceparent()
    {
        var headers = await HeadersSeenByTheServer(new SocketsHttpHandler());

        var traceparent = headers["traceparent"];
        Assert.False(string.IsNullOrEmpty(traceparent),
            "The default SocketsHttpHandler did NOT propagate a traceparent, so TheShippedHandler_SendsNoTraceparent " +
            "proves nothing. Do not trust that test until this one passes.");
        Assert.StartsWith("00-", traceparent);
    }
}
