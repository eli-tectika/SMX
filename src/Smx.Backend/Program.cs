using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Files.DataLake;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Trace;
using Smx.Backend.Agents;
using Smx.Backend.Api;
using Smx.Backend.Extraction;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Documents;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Infrastructure.Search;
using Smx.Infrastructure.Sds;

// `LearnedConclusionsIndex` is BOTH a type (the AI Search write side) and a BackendOptions property (the
// index NAME). Alias the type so `new LcSearchIndex(client, opts.LearnedConclusionsIndex)` reads as what
// it is — a client over the index called `opts.LearnedConclusionsIndex`.
using LcSearchIndex = Smx.Infrastructure.Search.LearnedConclusionsIndex;

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
    BackendHost.ConfigureServices(builder.Services, builder.Configuration);

if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 })
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor()
        // The distro instruments ASP.NET Core, HttpClient and the Azure SDK; it does not know about the
        // agent framework's own sources. An agent run's spans are the only view there is of a stage that
        // hung, so they are named here.
        //
        // NAMED, never AddSource("*"). The wildcard subscribes to the RUNTIME's own diagnostic sources,
        // including System.Net.NameResolution — and the distro's standard-metrics processor calls
        // Dns.GetHostName() from inside its own OnEnd, whose resource is still null on re-entry:
        //   StandardMetricsExtractionProcessor.OnEnd → CreateAzureMonitorResource → Dns.GetHostName()
        //     → NameResolutionActivity.Stop → StandardMetricsExtractionProcessor.OnEnd → ...
        // That recursion is a StackOverflowException, which is not catchable and takes the whole process
        // with it — and since the orchestrator was folded in, that process is ALL of SMX, not just stage
        // dispatch. It does not fire on the pinned aspnet:8.0 base image only because those sources are
        // .NET 9+; with RollForward=Major a routine base-image bump would arm it.
        // See BackendTelemetryWiringTests, which fails if this list stops covering the agent sources.
        .WithTracing(t => t.AddSource(AgentTelemetry.Sources));

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
// The unified per-stage thread and its control surface (design §7): read, stream, message, cancel, rerun.
app.MapThreadEndpoints();
app.MapKnowledgeEndpoints();
app.MapDocumentEndpoints();
app.MapDosingEndpoints();
app.MapTableEndpoints();
app.MapAmendmentEndpoints();
app.MapIntakeSessionEndpoints();
// The interview's streaming turn. Served HERE, in the same process as the agent that answers it — it used
// to be an SSE relay from this app to a second one, and the relay existed for no other reason.
app.MapInterviewEndpoints();
app.MapAttachmentEndpoints();
app.MapIntakeBriefEndpoints();
app.MapXrfEndpoints();
app.MapDecisionEndpoints();
app.MapExportEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook

/// Everything this app needs a configured estate for: the Cosmos stores, the document access layer, and —
/// since the orchestrator was folded in — the agents, their tools and the run trail.
///
/// In one callable place so a test can actually BUILD it. `dotnet build` proves nothing about DI: a missing
/// registration is a runtime failure at the first resolve, and for half of this graph that means in
/// production, mid-interview or mid-stage. See Smx.Backend.Tests/BackendHostWiringTests.
public static class BackendHost
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
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id))
            : new DefaultAzureCredential();

        services.AddSingleton(opts);
        services.AddSingleton(credential);
        services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
        {
            // System.Text.Json (not the SDK's default Newtonsoft) — required to round-trip JsonElement
            // (ProjectDoc.Payload). See SystemTextJsonCosmosSerializer.
            Serializer = new SystemTextJsonCosmosSerializer(Json.Options),
        }));
        services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
        // The run trail. Its OWN container, not `record`: a run is execution history, not an analytical
        // output, and it must never appear on the record bus.
        services.AddSingleton<IRunStore>(sp => new CosmosRunStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RunContainer)));
        services.AddSingleton<IIntakeSessionStore>(sp => new CosmosIntakeSessionStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.IntakeSessionContainer)));

        // Read-only view over the SDS subsystem's corpus registry: the MSDS Registry surface
        // composes sheet facts from it at read time (design §6.3 — reference, don't duplicate).
        services.AddSingleton<ISdsCorpusReader>(sp => new CosmosSdsCorpusReader(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SdsRegistryContainer)));

        // Interview attachments live in the existing `bronze` ADLS filesystem. Registered only when
        // configured, exactly like the Cosmos stores above: the tests inject an InMemoryAttachmentBlobStore
        // and never construct a real client. Reuses the SAME `credential` built above; do not construct
        // a second one.
        if (configuration["BRONZE_ACCOUNT_NAME"] is { Length: > 0 } bronzeAccount)
        {
            var filesystem = configuration["BRONZE_FILESYSTEM"] ?? "bronze";
            services.AddSingleton<IAttachmentBlobStore>(_ => new BlobAttachmentStore(
                new DataLakeServiceClient(new Uri($"https://{bronzeAccount}.dfs.core.windows.net"), credential)
                    .GetFileSystemClient(filesystem)));
        }

        // ── Document access layer (the file viewer's read side) ─────────────────────────────────────
        // Everything below is read-only by construction: none of these types has a write method.
        //
        // sds-master-list, reg-registry, reg-state and reg-silver are literals rather than options
        // because they name the regsync Functions app's estate, not this app's configuration surface;
        // functions.bicep pins the same four names as literals on the writing side. SDS_REGISTRY_CONTAINER
        // is an option here only because the MSDS Registry surface already made it one. If a later change
        // makes these configurable, add them to BackendOptions alongside it — all five or none.
        services.AddSingleton<IDocumentContentStore>(_ =>
        {
            // BRONZE_LOCAL_PATH stands a directory in for the filesystem in local dev, and wins when both
            // are set. Neither set is a misconfiguration that has to name itself: an empty account name
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

        services.AddSingleton<ISdsDocumentSource>(sp =>
        {
            var cosmos = sp.GetRequiredService<CosmosClient>();
            return new CosmosSdsDocumentSource(
                cosmos.GetContainer(opts.CosmosDatabase, opts.SdsRegistryContainer),
                cosmos.GetContainer(opts.CosmosDatabase, "sds-master-list"));
        });

        services.AddSingleton<IRegDocumentSource>(sp =>
        {
            var cosmos = sp.GetRequiredService<CosmosClient>();
            return new CosmosRegDocumentSource(
                cosmos.GetContainer(opts.CosmosDatabase, "reg-registry"),
                cosmos.GetContainer(opts.CosmosDatabase, "reg-state"));
        });

        // What turns a retrieved regulatory chunk into a citation the operator can OPEN. A singleton because
        // it caches the (sourceId, docId) → document-id map; see RegDocumentIdIndex for why the id is looked
        // up rather than derived. It takes IRegDocumentSource and NOT RegDocumentProvider on purpose: the
        // provider needs an IDocumentContentStore it would never touch here, and resolving that eagerly would
        // let a deployment with no bronze account break regulatory RETRIEVAL, not just the viewer.
        services.AddSingleton<IRegDocumentIdIndex>(sp =>
            new RegDocumentIdIndex(sp.GetRequiredService<IRegDocumentSource>()));

        services.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(
            new SdsDocumentProvider(sp.GetRequiredService<ISdsDocumentSource>()),
            new RegDocumentProvider(sp.GetRequiredService<IRegDocumentSource>(),
                                    sp.GetRequiredService<IDocumentContentStore>())));

        services.AddSingleton<IDocumentTextReader>(sp => new CompositeDocumentTextReader(
            new CosmosRegSilverTextReader(
                sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "reg-silver")),
            // ClientOptions() is not optional: the SDK's default serializer is PascalCase and
            // case-sensitive, and would bind every field of a camelCase index row to null in silence.
            new SdsIndexTextReader(new SearchClient(
                new Uri(opts.SearchEndpoint), opts.SdsIndex, credential, SdsIndexTextReader.ClientOptions()))));

        // ── The agents, their tools, and the deterministic lookups they answer from ──────────────────
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
                cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer),
                cosmos.GetContainer(opts.CosmosDatabase, opts.SubstancePropertiesContainer));
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

        // A FACTORY, not an eagerly-constructed instance: it now depends on IRegDocumentIdIndex, and eager
        // construction would resolve that (and its Cosmos containers) at startup instead of at first search.
        services.AddSingleton<IRegulatorySearch>(sp => new RegulatorySearchTool(
            new SearchClient(new Uri(opts.SearchEndpoint), opts.RegulatoryIndex, credential),
            sp.GetRequiredService<IRegDocumentIdIndex>()));
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
        // Registered unconditionally so that switching to the anonymizing egress is a pure config flip
        // (WEB_SEARCH_PROVIDER=proxy) with no code change and no redeploy. Both singletons are lazy: in the
        // default hosted mode ToolBox never invokes the factory, so neither the proxy client nor WebSearchTool
        // is ever actually constructed.
        services.AddHttpClient(nameof(SearchProxyClient));
        services.AddSingleton<ISearchProxyClient>(sp => new SearchProxyClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SearchProxyClient)),
            sp.GetRequiredService<Azure.Core.TokenCredential>(),
            opts.SearchProxyEndpoint,
            opts.SearchProxyAudience,
            sp.GetRequiredService<ILogger<SearchProxyClient>>()));

        // SDS acquisition — the line to the regsync Function App, same shape as the proxy client above and
        // for the same reason (two plain strings, so DI cannot construct it by type).
        //
        // Registered unconditionally and fail-safe by construction: with no endpoint configured the client
        // simply cannot reach anything and reports every request as unavailable-with-a-reason, which is
        // precisely the contract the callers already handle. A missing deployment degrades the ability to
        // fetch a NEW sheet; it never blocks a stage and never breaks a run.
        services.AddHttpClient(nameof(SdsAcquisitionClient));
        services.AddSingleton<ISdsAcquisition>(sp => new SdsAcquisitionClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SdsAcquisitionClient)),
            sp.GetRequiredService<Azure.Core.TokenCredential>(),
            opts.SdsServiceEndpoint,
            opts.SdsServiceAudience,
            sp.GetRequiredService<ILogger<SdsAcquisitionClient>>()));

        services.AddSingleton<Func<SensitiveTerms, IWebSearch>>(sp => terms => new WebSearchTool(
            sp.GetRequiredService<ISearchProxyClient>(),
            terms,
            webEnabled,
            opts.WebSearchMaxPerStage,
            sp.GetRequiredService<ILogger<WebSearchTool>>()));
        // ToolBox takes the hosted-vs-proxy web-search flag as a bool, which DI cannot auto-resolve, so it is
        // constructed explicitly. opts.UseHostedWebSearch selects the built-in tool (default) vs the legacy proxy.
        services.AddSingleton(sp => new ToolBox(
            sp.GetRequiredService<ICatalogLookup>(),
            sp.GetRequiredService<ICompatibilityLookup>(),
            sp.GetRequiredService<IRegulatorySearch>(),
            sp.GetRequiredService<ISdsSearch>(),
            sp.GetRequiredService<IReferenceSearch>(),
            sp.GetRequiredService<IKnowledgeStore>(),
            sp.GetRequiredService<ILearnedConclusionsSearch>(),
            sp.GetRequiredService<Func<SensitiveTerms, IWebSearch>>(),
            opts.UseHostedWebSearch,
            // Without this the ensure_sds tool is absent from every tool list and the feature is dead in
            // production while its tests pass — the parameter is optional so the many test hosts that
            // construct a ToolBox need not know about SDS acquisition at all.
            sp.GetRequiredService<ISdsAcquisition>()));
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
            FoundryChatClientFactory.CreateAsync(opts, credential).GetAwaiter().GetResult());
        services.AddSingleton<IAgentRuns, AgentRuns>();

        // ChatTools IS DELIBERATELY NOT REGISTERED HERE, and that absence is a safety property — do not
        // "tidy" it into the container.
        //
        // PipelineRunner.OnChatMessageAsync constructs one PER TURN, closed over the (projectId, stage,
        // chatKey) of the chat-message it is answering. That closure is the cross-project write guard:
        // because the project is captured rather than passed, the model's tool schema offers no parameter
        // with which to NAME a project — so it can only act on the one it is talking about. A singleton
        // would have to take the project from somewhere ambient, and one hallucinated id would then mutate
        // a DIFFERENT project's analysis: a revision queued against records the operator never asked about,
        // no undo, and no reason for anyone to look. The per-turn binding turns "must not" into "cannot",
        // which is the only form of that rule worth having.
        //
        // (ChatAgent is static and needs nothing; AgentRuns already holds the IChatClient and the ToolBox a
        // chat turn reads with. So there is genuinely nothing else for chat to register — see
        // BackendHostWiringTests.AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage, which
        // builds a real turn's tool list out of this container rather than taking that on trust.)
        // The in-process fan-out from the runner to whoever is watching the thread stream. A singleton
        // because the runner and the SSE endpoint are the SAME process now — that is the whole point of
        // the service merge.
        services.AddSingleton<ThreadEventHub>();

        // The OPTIONAL trailing params are wired here deliberately — this is the "deferred production
        // wiring" the PipelineRunner XML docs point at. Without the IKnowledgeStore every metal loading
        // reads as unknown and every substance is dropped from the dose and named; without the
        // ICatalogLookup the supply audit stays empty rather than being fabricated. Both are the singletons
        // registered above; the E2E (DosingCostEndToEndTests) proves the logic, this line turns it on.
        services.AddSingleton(sp => new PipelineRunner(
            sp.GetRequiredService<IRecordStore>(), sp.GetRequiredService<IRunStore>(),
            sp.GetRequiredService<IAgentRuns>(), sp.GetRequiredService<ThreadEventHub>(),
            sp.GetRequiredService<ILearnedConclusionWriter>(), opts.RegulatoryParallelism,
            sp.GetRequiredService<ILogger<PipelineRunner>>(),
            sp.GetRequiredService<IKnowledgeStore>(), sp.GetRequiredService<ICatalogLookup>(),
            // Same reasoning: unpassed, the SDS ledger never learns about a substance a project put into
            // play, which is the gap that made MSDS coverage look arbitrary in the first place.
            sp.GetRequiredService<ISdsAcquisition>()));

        // ONE supervisor, resolved twice. The hosted-service registration MUST go through the container
        // rather than construct its own — `AddHostedService<PipelineSupervisor>()` would build a SECOND
        // instance, and then the endpoints' registry and the running pipelines would live in different
        // objects: every start would 202 over a pipeline no cancel could ever reach, and the boot resume
        // would run in an instance nothing else can see. BackendHostWiringTests asserts the identity.
        //
        // It lives here, not at the top of Program.cs, for the same reason PipelineRunner does: it depends
        // on the runner, which needs the whole agent graph and a configured estate. A test host that
        // registers an IRecordStore and nothing else has no supervisor — POST /start degrades to the status
        // flip there (see the note on that endpoint), and §7.3's three control endpoints 500 rather than
        // pretend.
        services.AddSingleton<PipelineSupervisor>();
        services.AddHostedService(sp => sp.GetRequiredService<PipelineSupervisor>());
    }
}
