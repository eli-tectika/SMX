using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// One host with Application Insights actually switched on, shared by the whole class. Built once, not per
/// test: an OpenTelemetry TracerProvider installs a process-wide ActivityListener, and a second host's
/// listener outliving its factory would make the negative assertions below sample true for the wrong reason.
public sealed class AppInsightsHostFixture : IDisposable
{
    // Well-formed but unroutable. The distro parses the connection string eagerly and exports on a
    // background timer, so a bad FORMAT would fail the build while a bad HOST merely never delivers.
    private const string ConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://telemetry.example.invalid/";

    private readonly WebApplicationFactory<Program> _factory =
        new WebApplicationFactory<Program>().WithWebHostBuilder(
            b => b.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", ConnectionString));

    public AppInsightsHostFixture() => _ = _factory.Services; // force the host to build and start the provider

    public void Dispose() => _factory.Dispose();
}

/// The telemetry wiring in Program.cs, which NO other test reaches: nothing else sets
/// APPLICATIONINSIGHTS_CONNECTION_STRING, so the whole `AddOpenTelemetry()` branch is dead code to the suite
/// and any change to it — including one that crashes the process — currently fails nothing.
///
/// It has already been wrong once. `AddSource("*")` subscribes to the runtime's own diagnostic sources, and
/// with System.Net.NameResolution among them the distro's standard-metrics processor recurses through
/// Dns.GetHostName() inside its own OnEnd until the stack overflows — uncatchable, and fatal to the single
/// process that is now all of SMX.
public class BackendTelemetryWiringTests(AppInsightsHostFixture _) : IClassFixture<AppInsightsHostFixture>
{
    /// Whether an activity on `name` would be recorded — i.e. whether the host's tracer provider subscribed
    /// to that source. StartActivity returns null when nothing is listening.
    private static bool IsSampled(string name)
    {
        using var source = new ActivitySource(name);
        using var activity = source.StartActivity("probe");
        return activity is not null;
    }

    /// The source names a REAL instrumented agent turn emits on, observed rather than declared.
    ///
    /// Asserting AgentTelemetry.Sources against itself would prove nothing at all. Reading the names off the
    /// SDK means this test also fails the day the agent framework renames its source (dropping the
    /// `Experimental.` prefix is a published intention) — which would otherwise take agent tracing away in
    /// silence, leaving a stage that hung with no span to show for it.
    private const string ProbeSource = "Smx.Tests.TelemetryProbe";

    private static async Task<IReadOnlyList<string>> SourceNamesARealAgentTurnEmitsOn()
    {
        var seen = new List<string>();
        // An ActivityListener is PROCESS-wide, and the rest of the suite is running in parallel. Scoping by
        // trace id is not tidiness: without it this collected whatever socket or DNS activity another test
        // happened to open at the same instant, and then demanded the host collect it — asserting the exact
        // opposite of the landmine test below, intermittently. Everything the agent turn emits is a child of
        // `root` and carries its trace id; nothing another test does can be.
        Activity? root = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (root is null || a.TraceId != root.TraceId || a.Source.Name == ProbeSource) return;
                lock (seen) if (!seen.Contains(a.Source.Name)) seen.Add(a.Source.Name);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var probe = new ActivitySource(ProbeSource);
        root = probe.StartActivity("agent-turn");
        Assert.NotNull(root); // the listener above is listening to everything, so this cannot be null

        // The instrumented forms of exactly what MafAgent and FoundryChatClientFactory construct: a MAF
        // ChatClientAgent over an IChatClient. Instrumentation is switched on HERE, in the test, because
        // the names are what is under test — not whether production has enabled emission yet.
        var chat = new FakeChatClient().AsBuilder().UseOpenTelemetry().Build();
        AIAgent agent = new ChatClientAgent(chat, instructions: "p", name: "probe", tools: []);
        agent = agent.AsBuilder().UseOpenTelemetry().Build();
        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        root.Stop();
        return seen;
    }

    [Fact]
    public async Task EverySourceARealAgentTurnEmitsOn_IsSampledByTheHost()
    {
        var emitted = await SourceNamesARealAgentTurnEmitsOn();

        // Both halves matter: the agent span says a turn ran, the inner Microsoft.Extensions.AI span carries
        // the model id, token counts and finish reason. A stage that hung looks identical to a stage that
        // was never triggered without them.
        Assert.Contains("Experimental.Microsoft.Agents.AI", emitted);
        Assert.Contains("Experimental.Microsoft.Extensions.AI", emitted);

        foreach (var name in emitted)
            Assert.True(IsSampled(name), $"the agent traces on '{name}' and the host does not collect it");
    }

    /// THE landmine, pinned by name. System.Net.NameResolution is a .NET 9+ ActivitySource, so it does not
    /// fire on the pinned aspnet:8.0 base image — but the csproj is RollForward=Major, this project already
    /// runs on net10, and a routine base-image bump would turn a subscription here into a crash loop of the
    /// only process SMX has.
    [Theory]
    [InlineData("System.Net.NameResolution")]
    [InlineData("Experimental.System.Net.Sockets")]
    [InlineData("Experimental.System.Net.Http.Connections")]
    public void TheRuntimesOwnDiagnosticSources_AreNotSubscribedTo(string runtimeSource)
        => Assert.False(IsSampled(runtimeSource),
            $"'{runtimeSource}' is subscribed — AddSource(\"*\") is back, and with it the " +
            "StandardMetricsExtractionProcessor → Dns.GetHostName() → OnEnd stack overflow");

    /// Proves the assertions above can fail at all: with a wildcard subscription every name samples true,
    /// including one nobody has ever registered, and both tests would pass while meaning nothing.
    [Fact]
    public void ASourceNobodyRegistered_IsNotSampled()
        => Assert.False(IsSampled("Smx.NoSuchSource.ThisIsTheControl"));

    /// Why AgentTelemetry.Sources does NOT list the Azure SDK: the distro already collects it. Pinned so
    /// that list cannot quietly grow a duplicate of what is handled for us — and so that the day the distro
    /// stops doing it, something says so rather than the Cosmos and Search calls simply vanishing.
    [Theory]
    [InlineData("Azure.Core.Http")]
    [InlineData("Azure.Identity.DefaultAzureCredential")]
    [InlineData("Azure.Search.Documents.SearchClient")]
    public void TheAzureSdksSources_ComeFromTheDistro_NotFromOurList(string azureSource)
    {
        Assert.True(IsSampled(azureSource));
        Assert.DoesNotContain(azureSource, Smx.Backend.Agents.AgentTelemetry.Sources);
    }
}
