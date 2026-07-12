using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Smx.Domain;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Infrastructure.Search;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;

var builder = Host.CreateApplicationBuilder(args);
var opts = BackendOptions.From(builder.Configuration);
if (string.IsNullOrEmpty(opts.SearchEndpoint))
    throw new InvalidOperationException("SEARCH_ENDPOINT missing — required for the agent host");
// FoundryChatClientFactory guards FOUNDRY_ENDPOINT itself.
Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
    ? new ManagedIdentityCredential(id)
    : new DefaultAzureCredential();

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(credential);
builder.Services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
{
    // System.Text.Json (not the SDK's default Newtonsoft) — required to round-trip JsonElement
    // (ProjectDoc.Payload + the ChangeFeedProcessor<JsonElement>). See SystemTextJsonCosmosSerializer.
    Serializer = new SystemTextJsonCosmosSerializer(Json.Options),
}));
builder.Services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
builder.Services.AddSingleton<ICompatibilityLookup>(sp => new CosmosCompatibilityLookup(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CompatibilityContainer)));
builder.Services.AddSingleton<ICatalogLookup>(sp => new CosmosCatalogLookup(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CatalogContainer)));
builder.Services.AddSingleton<IRegulatorySearch>(new RegulatorySearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.RegulatoryIndex, credential)));
builder.Services.AddSingleton<ISdsSearch>(new SdsSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.SdsIndex, credential)));
builder.Services.AddSingleton<IReferenceSearch>(new ReferenceSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.ReferenceIndex, credential)));
builder.Services.AddSingleton<ToolBox>();
builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
    FoundryChatClientFactory.CreateAsync(opts, credential).GetAwaiter().GetResult());
builder.Services.AddSingleton<IAgentRuns, AgentRuns>();
builder.Services.AddSingleton(sp => new StageDispatcher(
    sp.GetRequiredService<IRecordStore>(), sp.GetRequiredService<IAgentRuns>(), opts.ScreeningParallelism));
builder.Services.AddHostedService<ChangeFeedWorker>();

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
