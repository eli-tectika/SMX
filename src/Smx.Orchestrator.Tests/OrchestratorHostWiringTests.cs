using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Orchestrator.Agents;
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

    /// The chat turn, built from the REAL container — the exact path that runs when the change feed hands the
    /// orchestrator its first ChatMessageDoc in Azure.
    ///
    /// Resolving StageDispatcher above proves the DISPATCH graph; it does not prove this one. A chat turn
    /// reaches further: StageDispatcher constructs a ChatTools, and AgentRuns.RunChatAsync pairs the stage's
    /// READ tools (ToolBox, seven injected dependencies of its own) with that turn's MUTATING tools. A read
    /// tool whose dependency nobody registered is a container that builds, a host that starts, an intake that
    /// runs — and a crash on the first thing the operator ever says. That failure is invisible to every test
    /// that fakes IAgentRuns, which is all of them.
    [Fact]
    public void AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage()
    {
        using var sp = Build(Config());
        var toolBox = sp.GetRequiredService<ToolBox>();
        var store = sp.GetRequiredService<IRecordStore>();

        foreach (var stage in Stages.All)
        {
            // Exactly what StageDispatcher.OnChatMessageAsync constructs: bound to one project, one stage, and
            // the key of the message being answered.
            var chatTools = new ChatTools(store, "p1", stage, "abcd1234");
            var readTools = toolBox.ReadToolsFor(stage);
            var tools = AgentRuns.ChatTurnTools(toolBox, chatTools);

            // The READ half is asserted SEPARATELY, and that is the whole point of this loop. Asserting only
            // that the combined list is non-empty proves nothing: on a revisable stage ChatTools contributes
            // `apply_revision` on its own, so the entire retrieval surface could vanish and the combined list
            // would still be non-empty. (Mutation-tested: deleting `Stages.Discovery => DiscoveryTools()` from
            // ReadToolsFor left the combined-list assertion green.) An agent that can still CHANGE the analysis
            // but can no longer LOOK ANYTHING UP is the worst reachable state in this system — it answers, and
            // acts, from memory.
            if (stage == Stages.Matrix)
            {
                // Fail-closed by design: Matrix derives its output from the record it is handed, so there is no
                // corpus to search, and it is not revisable — a Matrix turn holds no capability at all.
                Assert.Empty(readTools);
                Assert.Empty(tools);
            }
            else
            {
                Assert.NotEmpty(readTools);
            }

            // Nothing is dropped on the floor between the two halves — the turn gets exactly what ToolBox and
            // ChatTools each hand over, out of THIS container.
            Assert.Equal(readTools.Count + chatTools.Tools().Count, tools.Count);
        }
    }

    /// ChatTools is NOT in the container, and that absence is a SAFETY property — see the comment on its
    /// registration-that-isn't in OrchestratorHost.ConfigureServices.
    ///
    /// It is constructed per turn, closed over (projectId, stage, chatKey), which is the cross-project write
    /// guard: the model is offered no parameter with which to name a project, so a hallucinated id cannot
    /// mutate a different project's analysis. A singleton would have to take those from somewhere ambient —
    /// and the day it did, one turn's tools would be able to write another project's records, silently, with
    /// no undo and no reason for anyone to look. This test is what stops that being "tidied" in.
    [Fact]
    public void ChatTools_IsNotAContainerService_ItIsConstructedPerTurn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        OrchestratorHost.ConfigureServices(services, Config());

        // Asserted on the DESCRIPTORS, not just on a null resolve: a resolve can come back null for reasons
        // that have nothing to do with intent, but a descriptor is somebody having deliberately registered it.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ChatTools));

        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<ChatTools>());
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
}
