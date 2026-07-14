using System.Diagnostics;

namespace Smx.SearchProxy.Providers;

/// The outbound handler for the ONE client in this app that talks to the public internet.
///
/// It is a factory, not an inline lambda in Program.cs, for one reason: the invariant it carries has to be
/// testable against the REAL handler chain. A stub HttpMessageHandler sits ABOVE the runtime's
/// DiagnosticsHandler — the component that injects `traceparent` — so a test built on a stub observes the
/// request before the header would ever be added and passes whether or not we suppress it. The thing under
/// test must be the thing that ships (see TracePropagationTests).
public static class ProxyHttp
{
    /// Suppresses the W3C `traceparent` header .NET injects into every outbound request whenever an Activity
    /// is in scope — and under Application Insights, one always is.
    ///
    /// Sent to Brave, `traceparent` is not merely a correlation handle across our requests. It carries ONE
    /// trace id for the whole cover batch, which tells the provider exactly which N queries were issued
    /// together — collapsing the haystack into "these 4 are one caller's batch" and defeating the k-anonymity
    /// outright. The decoys would still be sent; they would just stop working.
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    };
}
