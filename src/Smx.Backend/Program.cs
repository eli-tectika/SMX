using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Cosmos;
using Smx.Backend.Api;
using Smx.Domain;
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
            cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.SubstancePropertiesContainer));
    });
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
app.MapRevisionEndpoints();
app.MapChatEndpoints();
app.MapKnowledgeEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
