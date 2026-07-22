using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Files.DataLake;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Cosmos;
using Smx.Backend.Api;
using Smx.Backend.Extraction;
using Smx.Domain;
using Smx.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// The extractor set. Order does not matter (no two claim the same extension), but a new extractor is
// added HERE and nowhere else — TextExtraction picks the first that CanHandle.
builder.Services.AddSingleton(new TextExtraction(
    [new PlainTextExtractor(), new PdfExtractor(), new DocxExtractor(), new XlsxExtractor()]));

// Auth is conditional on config, mirroring the Cosmos wiring below: no ENTRA_TENANT_ID/API_CLIENT_ID
// means no auth, so every existing endpoint test (which sets neither) stays green.
var tenantId = builder.Configuration["ENTRA_TENANT_ID"];
var apiClientId = builder.Configuration["API_CLIENT_ID"];
var authEnabled = tenantId is { Length: > 0 } && apiClientId is { Length: > 0 };
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.TokenValidationParameters.ValidAudiences = [ apiClientId!, $"api://{apiClientId}" ];
            // Accept both issuer forms as defense-in-depth: configure-auth.sh pins the API app's
            // requestedAccessTokenVersion=2 so Entra issues v2 tokens (iss = the v2.0 endpoint above), but
            // if a v1 token ever arrives (e.g. the pin drifts or predates this fix) it should still
            // validate rather than 401 every authenticated call. Signing keys still come from Authority's
            // OIDC metadata; this only broadens the accepted-issuer set.
            options.TokenValidationParameters.ValidIssuers =
            [
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/",
            ];
        });
    // Every endpoint requires an authenticated user unless it opts out with AllowAnonymous (/healthz).
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}

// Production wiring only when configured; tests inject InMemoryRecordStore instead.
if (builder.Configuration["COSMOS_ACCOUNT_ENDPOINT"] is { Length: > 0 })
{
    var opts = BackendOptions.From(builder.Configuration);
    Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
        ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id))
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
            cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.SubstancePropertiesContainer));
    });

    // Read-only view over the SDS subsystem's corpus registry: the MSDS Registry surface
    // composes sheet facts from it at read time (design §6.3 — reference, don't duplicate).
    builder.Services.AddSingleton<ISdsCorpusReader>(sp =>
        new CosmosSdsCorpusReader(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SdsRegistryContainer)));

    builder.Services.AddSingleton<IIntakeSessionStore>(sp => new CosmosIntakeSessionStore(
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.IntakeSessionContainer)));

    // Interview attachments live in the existing `bronze` ADLS filesystem. Registered only when
    // configured, exactly like the Cosmos stores above: the tests inject an InMemoryAttachmentBlobStore
    // and never construct a real client.
    if (builder.Configuration["BRONZE_ACCOUNT_NAME"] is { Length: > 0 } bronzeAccount)
    {
        var filesystem = builder.Configuration["BRONZE_FILESYSTEM"] ?? "bronze";
        builder.Services.AddSingleton<IAttachmentBlobStore>(_ => new BlobAttachmentStore(
            new DataLakeServiceClient(new Uri($"https://{bronzeAccount}.dfs.core.windows.net"), credential)
                .GetFileSystemClient(filesystem)));
    }
}

if (builder.Configuration["ORCHESTRATOR_BASE_URL"] is { Length: > 0 } orchestratorUrl)
{
    builder.Services.AddHttpClient(IntakeSessionEndpoints.OrchestratorClient, c =>
    {
        c.BaseAddress = new Uri(orchestratorUrl);
        // An interview turn is a model call with tool round-trips. The default 100 s is a plausible
        // real duration here, not a pathological one.
        c.Timeout = TimeSpan.FromMinutes(5);
    });
}
else
{
    // Tests set no ORCHESTRATOR_BASE_URL and never exercise the proxy. Registering the factory anyway
    // keeps DI resolvable so the ROUTE still builds — an unregistered IHttpClientFactory would break
    // endpoint construction for the whole app (see trap 1).
    builder.Services.AddHttpClient();
}
if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 })
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();
// App Gateway path-based routing forwards /api/* WITHOUT stripping the prefix, so serve under it.
if (app.Configuration["PATH_BASE"] is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase);
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.Logger.LogInformation("Entra auth ENABLED — validating bearer tokens on all endpoints except /healthz.");
}
else
{
    app.Logger.LogInformation("Entra auth DISABLED — ENTRA_TENANT_ID/API_CLIENT_ID not set; all endpoints are open.");
}
app.MapProjectEndpoints();
app.MapProjectsListEndpoints();
app.MapRevisionEndpoints();
app.MapChatEndpoints();
app.MapKnowledgeEndpoints();
app.MapDosingEndpoints();
app.MapCostEndpoints();
app.MapIntakeSessionEndpoints();
app.MapAttachmentEndpoints();
app.MapIntakeBriefEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
