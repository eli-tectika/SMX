using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;

namespace Smx.Orchestrator.Tests;

/// The host's DI graph, actually built. `dotnet build` cannot catch a missing registration — the orchestrator
/// resolves StageDispatcher only when the change feed hands it a document, so an unregistered dependency is a
/// production-only crash. (It was one: StageDispatcher took an ILearnedConclusionWriter that nothing registered,
/// and every test stayed green.) These tests resolve the graph the way the running host does.
public class OrchestratorHostWiringTests
{
    /// Config that lets every client CONSTRUCT without touching the network. The Azure SDK clients here are
    /// lazy — CosmosClient, AzureOpenAIClient, SearchClient and the Anthropic Foundry client all defer I/O to
    /// the first call — so a well-formed endpoint is all the container needs. FOUNDRY_API_KEY keeps
    /// FoundryChatClientFactory on its explicit-key branch (no Key Vault round-trip, no token acquisition).
    private static IConfiguration Config(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://smx-test.documents.azure.com:443/",
            ["SEARCH_ENDPOINT"] = "https://smx-test.search.windows.net",
            ["FOUNDRY_ENDPOINT"] = "https://smx-test.services.ai.azure.com",
            ["FOUNDRY_API_KEY"] = "not-a-real-key",
            ["UAMI_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000",
        };
        foreach (var (k, v) in overrides) settings[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider Build(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        OrchestratorHost.ConfigureServices(services, config);
        return services.BuildServiceProvider();
    }

    /// The one field of a given type on an instance — used to prove that two collaborators were handed the
    /// SAME dependency instance. By type, not by name: primary-constructor capture fields are compiler-named.
    private static T Captured<T>(object instance) where T : class
    {
        var field = instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(f => typeof(T).IsAssignableFrom(f.FieldType));
        return (T)field.GetValue(instance)!;
    }

    [Fact]
    public void Host_ResolvesTheWholeDispatchGraph()
    {
        using var sp = Build(Config());

        // StageDispatcher is the one the host actually resolves at change-feed time — and the one that was
        // unresolvable until ILearnedConclusionWriter got registered.
        Assert.NotNull(sp.GetRequiredService<StageDispatcher>());
        Assert.NotNull(sp.GetRequiredService<ILearnedConclusionsSearch>());
        Assert.NotNull(sp.GetRequiredService<ILearnedConclusionsIndex>());
        Assert.NotNull(sp.GetRequiredService<ILearnedConclusionWriter>());
        Assert.NotNull(sp.GetRequiredService<IEmbedder>());
    }

    [Fact]
    public void BothSidesOfTheLearnedConclusionsLoop_ShareOneEmbedderInstance()
    {
        // THE assertion. The reader vectorizes the agent's query; the writer vectorizes the conclusion it
        // pushes. Two different embedding models put those vectors in different spaces — cosine similarity
        // between them is meaningless, the vector leg of every hybrid search returns noise, and NOTHING
        // errors. Retrieval just quietly gets worse. Same instance ⇒ same model, structurally.
        using var sp = Build(Config());

        var embedder = sp.GetRequiredService<IEmbedder>();
        var readSide = sp.GetRequiredService<ILearnedConclusionsSearch>();
        var writeSide = sp.GetRequiredService<ILearnedConclusionWriter>();

        Assert.Same(embedder, Captured<IEmbedder>(readSide));
        Assert.Same(embedder, Captured<IEmbedder>(writeSide));
    }

    [Fact]
    public void ConclusionWriter_GetsTheAiSearchIndexAndTheKnowledgeStore_TheSameOnesTheContainerHolds()
    {
        using var sp = Build(Config());
        var writer = sp.GetRequiredService<ILearnedConclusionWriter>();

        // Cosmos is authoritative, the index is its retrievable projection — the writer must hold both,
        // or a conclusion is written-but-unfindable (or findable-but-unrecorded).
        Assert.Same(sp.GetRequiredService<ILearnedConclusionsIndex>(), Captured<ILearnedConclusionsIndex>(writer));
        Assert.Same(sp.GetRequiredService<Smx.Domain.IKnowledgeStore>(), Captured<Smx.Domain.IKnowledgeStore>(writer));
    }

    [Fact]
    public void MissingFoundryEndpoint_ThrowsTheNamedSetting_NotAUriFormatException()
    {
        // The embedder's AzureOpenAIClient is constructed eagerly and needs a parseable URI. Without this
        // guard the host dies on `new Uri("")` — an opaque UriFormatException from a client nobody mentioned.
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(("FOUNDRY_ENDPOINT", ""))));
        Assert.Contains("FOUNDRY_ENDPOINT", ex.Message);
    }

    [Fact]
    public void MissingSearchEndpoint_ThrowsTheNamedSetting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(("SEARCH_ENDPOINT", ""))));
        Assert.Contains("SEARCH_ENDPOINT", ex.Message);
    }

    private const string ProxyEndpoint = "https://func-smx-searchproxy-dev.azurewebsites.net";

    /// A FACTORY, not a singleton: the tool closes over one project's sensitive terms and one stage's query
    /// budget. A singleton would hand project B the tool built for project A — the budget shared, and the term
    /// guard protecting the wrong client.
    [Fact]
    public void WebSearchFactory_IsRegistered_AndBuildsAFreshPerProjectTool()
    {
        using var sp = Build(Config(("SEARCH_PROXY_ENDPOINT", ProxyEndpoint)));
        var factory = sp.GetRequiredService<Func<SensitiveTerms, IWebSearch>>();

        var one = factory(new SensitiveTerms(["Acme"]));
        var two = factory(new SensitiveTerms(["Globex"]));
        Assert.NotNull(one);
        Assert.NotSame(one, two);
    }

    /// With a proxy configured the tool is LIVE — and bound to the terms it was built with. Proven without a
    /// network call: WebSearchTool checks the kill switch FIRST and the term guard SECOND, so a query carrying
    /// the client's name comes back refused-by-the-guard rather than disabled. A disabled tool would have said
    /// "disabled" instead, and a tool built with the wrong terms would have let the name through to egress.
    [Fact]
    public async Task WithAProxyConfigured_TheToolIsLive_AndGuardsThatProjectsTerms()
    {
        using var sp = Build(Config(("SEARCH_PROXY_ENDPOINT", ProxyEndpoint)));
        var web = sp.GetRequiredService<Func<SensitiveTerms, IWebSearch>>()(new SensitiveTerms(["Acme Bottling"]));

        var result = await web.SearchAsync("Acme Bottling yttrium marker forms", "discovery.candidate_forms");

        Assert.Empty(result.Hits);
        Assert.Contains("identifies this project", result.Note);
    }

    /// THE fail-safe. A deployment with no proxy must degrade to catalog-only — never fail to start, and never
    /// silently egress to an unconfigured endpoint. The tool SAYS it is disabled: an agent told nothing came
    /// back would conclude nothing is out there, and confidently exclude a good marker.
    [Fact]
    public async Task WithNoProxyConfigured_TheToolIsDisabled_AndSaysSo()
    {
        using var sp = Build(Config());   // no SEARCH_PROXY_ENDPOINT
        var web = sp.GetRequiredService<Func<SensitiveTerms, IWebSearch>>()(SensitiveTerms.None);

        var result = await web.SearchAsync("yttrium marker forms", "discovery.candidate_forms");

        Assert.Empty(result.Hits);
        Assert.Contains("disabled", result.Note);
    }

    /// The operator kill switch, honored even when a proxy IS configured.
    [Fact]
    public async Task WebSearchDisabledByTheKillSwitch_NeverReachesTheProxy()
    {
        using var sp = Build(Config(("SEARCH_PROXY_ENDPOINT", ProxyEndpoint), ("WEB_SEARCH_ENABLED", "false")));
        var web = sp.GetRequiredService<Func<SensitiveTerms, IWebSearch>>()(SensitiveTerms.None);

        var result = await web.SearchAsync("yttrium marker forms", "discovery.candidate_forms");

        Assert.Empty(result.Hits);
        Assert.Contains("disabled", result.Note);
    }
}
