using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.Cosmos;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Production wiring only when configured; tests inject InMemoryRecordStore instead.
if (builder.Configuration["COSMOS_ACCOUNT_ENDPOINT"] is { Length: > 0 })
{
    var opts = BackendOptions.From(builder.Configuration);
    Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
        ? new ManagedIdentityCredential(id)
        : new DefaultAzureCredential();
    builder.Services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
    {
        // System.Text.Json (not the SDK's default Newtonsoft) — required to round-trip JsonElement
        // (ProjectDoc.Payload + the ChangeFeedProcessor<JsonElement>). See SystemTextJsonCosmosSerializer.
        Serializer = new SystemTextJsonCosmosSerializer(Json.Options),
    }));
    builder.Services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
    builder.Services.AddSingleton<IKnowledgeStore>(sp =>
    {
        var cosmos = sp.GetRequiredService<CosmosClient>();
        return new CosmosKnowledgeStore(
            cosmos.GetContainer(opts.CosmosDatabase, opts.LearnedConclusionsContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.MarkerLibraryContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer));
    });
}
if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 })
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();
// App Gateway path-based routing forwards /api/* WITHOUT stripping the prefix, so serve under it.
if (app.Configuration["PATH_BASE"] is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase);
app.MapProjectEndpoints();
app.MapKnowledgeEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
