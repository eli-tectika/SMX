using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Search.Documents;
using Azure.Storage.Files.DataLake;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Cosmos;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Domain.Documents;
using Smx.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

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

    // ── Document access layer (the file viewer's read side) ─────────────────────────────────────
    // Everything below is read-only by construction: none of these types has a write method.
    //
    // sds-master-list, reg-registry, reg-state and reg-silver are literals rather than options
    // because they name the regsync Functions app's estate, not this app's configuration surface —
    // the same four names functions.bicep hardcodes. SDS_REGISTRY_CONTAINER is an option only
    // because the MSDS Registry surface already made it one.
    builder.Services.AddSingleton<IDocumentContentStore>(_ =>
    {
        // BRONZE_LOCAL_PATH stands a directory in for the filesystem in local dev, and wins when both
        // are set. Neither set is a misconfiguration that must name itself: an empty account name
        // resolves to "https://.dfs.core.windows.net" and would otherwise surface as an opaque DNS
        // failure on the operator's first click.
        if (opts.BronzeLocalPath is { Length: > 0 }) return new LocalBronzeDocumentStore(opts.BronzeLocalPath);
        if (opts.BronzeAccountName is not { Length: > 0 })
            throw new InvalidOperationException(
                "BRONZE_ACCOUNT_NAME is not set (BRONZE_LOCAL_PATH is the local-dev alternative) — " +
                "the document viewer has no bronze filesystem to read.");
        // The workload UAMI already holds Storage Blob Data Contributor at account scope
        // (infra/modules/data.bicep). The missing half was this code, not the permission.
        return new BronzeDocumentStore(
            new DataLakeServiceClient(new Uri($"https://{opts.BronzeAccountName}.dfs.core.windows.net"), credential)
                .GetFileSystemClient(opts.BronzeFilesystem));
    });

    builder.Services.AddSingleton<ISdsDocumentSource>(sp =>
    {
        var cosmos = sp.GetRequiredService<CosmosClient>();
        return new CosmosSdsDocumentSource(
            cosmos.GetContainer(opts.CosmosDatabase, opts.SdsRegistryContainer),
            cosmos.GetContainer(opts.CosmosDatabase, "sds-master-list"));
    });

    builder.Services.AddSingleton<IRegDocumentSource>(sp =>
    {
        var cosmos = sp.GetRequiredService<CosmosClient>();
        return new CosmosRegDocumentSource(
            cosmos.GetContainer(opts.CosmosDatabase, "reg-registry"),
            cosmos.GetContainer(opts.CosmosDatabase, "reg-state"));
    });

    builder.Services.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(
        new SdsDocumentProvider(sp.GetRequiredService<ISdsDocumentSource>()),
        new RegDocumentProvider(sp.GetRequiredService<IRegDocumentSource>(),
                                sp.GetRequiredService<IDocumentContentStore>())));

    builder.Services.AddSingleton<IDocumentTextReader>(sp =>
    {
        // SDS chunks live in the AI Search index, not in Cosmos, so an unset SEARCH_ENDPOINT would
        // reach `new Uri("")` and throw "the URI is empty" from somewhere that names nothing.
        if (opts.SearchEndpoint is not { Length: > 0 })
            throw new InvalidOperationException(
                "SEARCH_ENDPOINT is not set — the document viewer reads SDS chunk text from the sds-index.");
        return new CompositeDocumentTextReader(
            new CosmosRegSilverTextReader(
                sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "reg-silver")),
            new SdsIndexTextReader(new SearchClient(new Uri(opts.SearchEndpoint), opts.SdsIndex, credential)));
    });
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
app.MapDocumentEndpoints();
app.MapDosingEndpoints();
app.MapCostEndpoints();
app.MapIntakeSessionEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
