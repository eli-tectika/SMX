using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Smx.Domain;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Infrastructure.Search;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;

// `LearnedConclusionsIndex` is BOTH a type (the AI Search write side) and a BackendOptions property (the
// index NAME). Alias the type so `new LcSearchIndex(client, opts.LearnedConclusionsIndex)` reads as what
// it is — a client over the index called `opts.LearnedConclusionsIndex`.
using LcSearchIndex = Smx.Infrastructure.Search.LearnedConclusionsIndex;

var builder = Host.CreateApplicationBuilder(args);
OrchestratorHost.ConfigureServices(builder.Services, builder.Configuration);

if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 } aiConn)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .AddSource("*") // MAF + Azure SDK activity sources
            .AddHttpClientInstrumentation()
            .AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConn))
        .WithMetrics(m => m.AddAzureMonitorMetricExporter(o => o.ConnectionString = aiConn));
}

await builder.Build().RunAsync();

/// The agent host's container, in one callable place so a test can actually BUILD it. `dotnet build` proves
/// nothing about DI: a missing registration is a runtime failure at the first resolve, and this host resolves
/// StageDispatcher only when the change feed hands it a document — i.e. in production. See
/// Smx.Orchestrator.Tests/OrchestratorHostWiringTests.
public static class OrchestratorHost
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var opts = BackendOptions.From(configuration);
        if (string.IsNullOrEmpty(opts.SearchEndpoint))
            throw new InvalidOperationException("SEARCH_ENDPOINT missing — required for the agent host");
        // Guarded HERE, not deferred to FoundryChatClientFactory: the embedder's AzureOpenAIClient is
        // constructed eagerly below and needs a parseable URI, so an unset FOUNDRY_ENDPOINT would surface as
        // an opaque UriFormatException from a client nobody mentioned instead of the missing setting.
        if (string.IsNullOrEmpty(opts.FoundryEndpoint))
            throw new InvalidOperationException("FOUNDRY_ENDPOINT missing — required for the agent host (chat + embeddings)");

        Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
            ? new ManagedIdentityCredential(id)
            : new DefaultAzureCredential();

        services.AddSingleton(opts);
        services.AddSingleton(credential);
        services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
        {
            // System.Text.Json (not the SDK's default Newtonsoft) — required to round-trip JsonElement
            // (ProjectDoc.Payload + the ChangeFeedProcessor<JsonElement>). See SystemTextJsonCosmosSerializer.
            Serializer = new SystemTextJsonCosmosSerializer(Json.Options),
        }));
        services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
        services.AddSingleton<ICompatibilityLookup>(sp => new CosmosCompatibilityLookup(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CompatibilityContainer)));
        services.AddSingleton<ICatalogLookup>(sp => new CosmosCatalogLookup(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CatalogContainer)));
        services.AddSingleton<IKnowledgeStore>(sp =>
        {
            var cosmos = sp.GetRequiredService<CosmosClient>();
            return new CosmosKnowledgeStore(
                cosmos.GetContainer(opts.CosmosDatabase, opts.LearnedConclusionsContainer),
                cosmos.GetContainer(opts.CosmosDatabase, opts.MarkerLibraryContainer),
                cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer));
        });

        // ONE embedder, resolved from the container on BOTH sides of the learned-conclusions loop:
        // LearnedConclusionsSearchTool vectorizes the agent's QUERY, LearnedConclusionWriter vectorizes the
        // CONCLUSION it pushed. Both legs must use the SAME embedding model, or the query vector and the
        // document vectors live in different spaces: cosine similarity between them is meaningless, the
        // vector half of every hybrid search returns noise, and nothing errors — retrieval just silently
        // degrades. A single shared singleton makes that structural instead of conventional; do not
        // construct a second FoundryEmbedder anywhere.
        services.AddSingleton<IEmbedder>(new FoundryEmbedder(
            new AzureOpenAIClient(new Uri(opts.ResolvedOpenAiEndpoint), credential), opts.EmbeddingDeployment));

        services.AddSingleton<ILearnedConclusionsSearch>(sp => new LearnedConclusionsSearchTool(
            new SearchClient(new Uri(opts.SearchEndpoint), opts.LearnedConclusionsIndex, credential),
            sp.GetRequiredService<IEmbedder>()));                                   // read side
        services.AddSingleton<ILearnedConclusionsIndex>(new LcSearchIndex(
            new SearchIndexClient(new Uri(opts.SearchEndpoint), credential, new SearchClientOptions
            {
                // camelCase so LearnedConclusionChunk maps onto the index's field names (id, content,
                // contentVector, …). The chunk also pins them with [JsonPropertyName], so this is
                // belt-and-braces — but the Functions writers rely on exactly this, and a mismatch here
                // means the reader finds nothing, silently.
                Serializer = new JsonObjectSerializer(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            }),
            opts.LearnedConclusionsIndex));                                          // write side (index name)
        services.AddSingleton<ILearnedConclusionWriter, LearnedConclusionWriter>();  // Cosmos + index, same IEmbedder

        services.AddSingleton<IRegulatorySearch>(new RegulatorySearchTool(
            new SearchClient(new Uri(opts.SearchEndpoint), opts.RegulatoryIndex, credential)));
        services.AddSingleton<ISdsSearch>(new SdsSearchTool(
            new SearchClient(new Uri(opts.SearchEndpoint), opts.SdsIndex, credential)));
        services.AddSingleton<IReferenceSearch>(new ReferenceSearchTool(
            new SearchClient(new Uri(opts.SearchEndpoint), opts.ReferenceIndex, credential)));
        // Web search. The tool is built PER PROJECT (it closes over that project's sensitive terms and its own
        // stage budget), so what DI holds is a factory, not an instance.
        //
        // Fail-safe by construction: with no endpoint configured there is no proxy to call, so the tool reports
        // itself disabled and Discovery falls back to the catalog. A missing deployment must degrade the system,
        // not break it — and it must never silently egress instead.
        var webEnabled = opts.WebSearchEnabled && !string.IsNullOrEmpty(opts.SearchProxyEndpoint);

        // SearchProxyClient takes (HttpClient, TokenCredential, endpoint, audience, ILogger). The two strings
        // mean a typed-client registration cannot construct it, so name the client and build it explicitly.
        services.AddHttpClient(nameof(SearchProxyClient));
        services.AddSingleton<ISearchProxyClient>(sp => new SearchProxyClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SearchProxyClient)),
            sp.GetRequiredService<Azure.Core.TokenCredential>(),
            opts.SearchProxyEndpoint,
            opts.SearchProxyAudience,
            sp.GetRequiredService<ILogger<SearchProxyClient>>()));

        services.AddSingleton<Func<SensitiveTerms, IWebSearch>>(sp => terms => new WebSearchTool(
            sp.GetRequiredService<ISearchProxyClient>(),
            terms,
            webEnabled,
            opts.WebSearchMaxPerStage,
            sp.GetRequiredService<ILogger<WebSearchTool>>()));
        services.AddSingleton<ToolBox>();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
            FoundryChatClientFactory.CreateAsync(opts, credential).GetAwaiter().GetResult());
        services.AddSingleton<IAgentRuns, AgentRuns>();
        services.AddSingleton(sp => new StageDispatcher(
            sp.GetRequiredService<IRecordStore>(), sp.GetRequiredService<IAgentRuns>(),
            sp.GetRequiredService<ILearnedConclusionWriter>(), opts.RegulatoryParallelism));
        services.AddHostedService<ChangeFeedWorker>();
    }
}
