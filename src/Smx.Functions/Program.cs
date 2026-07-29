using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Files.DataLake;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Ingestion;
using Smx.Functions.Reg.Sourcing;
using Smx.Functions.Reference.Config;
using Smx.Functions.Reference.Data;
using Smx.Functions.Reference.Ingestion;
using Smx.Functions.Reference.Seeding;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        // Worker-side Application Insights. On Flex Consumption the host emits almost no telemetry
        // on the app's behalf — without this wiring NOTHING reaches the workspace (verified live
        // 2026-07-16: zero requests/traces despite a correct connection string).
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var opts = SdsOptions.From(ctx.Configuration);
        services.AddSingleton(opts);

        TokenCredential cred = string.IsNullOrEmpty(opts.UamiClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(opts.UamiClientId);
        services.AddSingleton(cred);

        // Suppliers + strategies + resolver. The supplier list lives in Cosmos (see SupplierCatalog for
        // why it is loaded lazily rather than here); the bundled file is its seed and its fallback.
        services.AddSingleton<ISourceStrategy, CasTemplateStrategy>();
        services.AddSingleton<ISourceStrategy, ProductLookupStrategy>();
        services.AddSingleton<ISourceStrategy, StaticMapStrategy>();

        // Web discovery — the fallback for substances no curated template covers. No key (or a dry run)
        // is a supported state: the dry-run search finds nothing and the resolver degrades to the
        // curated walk. Registered as itself, NOT as ISourceStrategy: it has no allowlist row, so being
        // in that collection would only mean sitting in a dictionary nothing looks up.
        services.AddHttpClient();
        if (opts.DryRun || string.IsNullOrWhiteSpace(opts.SearchApiKey))
            services.AddSingleton<ISdsWebSearch>(sp =>
                new DryRunSdsWebSearch(sp.GetRequiredService<ILogger<DryRunSdsWebSearch>>()));
        else
            services.AddSingleton<ISdsWebSearch>(sp => new BraveSdsWebSearch(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                opts.SearchApiKey,
                sp.GetRequiredService<ILogger<BraveSdsWebSearch>>()));
        services.AddSingleton(sp => new WebDiscoveryStrategy(sp.GetRequiredService<ISdsWebSearch>()));

        // Constructed by hand rather than by type: the built-in container does not honour a
        // default parameter value, so an implicit registration could never see the optional strategy.
        services.AddSingleton(sp => new SourceResolver(
            sp.GetRequiredService<SupplierCatalog>(),
            sp.GetServices<ISourceStrategy>(),
            sp.GetRequiredService<WebDiscoveryStrategy>()));

        // Cosmos (camelCase so records map to /element, /cas partition keys + registry field queries)
        services.AddSingleton(_ => new CosmosClient(opts.CosmosEndpoint, cred, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }
        }));
        services.AddSingleton<IMasterListStore>(sp => new CosmosMasterListStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.MasterContainer)));
        services.AddSingleton<IRegistryStore>(sp => new CosmosRegistryStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RegistryContainer)));
        services.AddSingleton<ISupplierStore>(sp => new CosmosSupplierStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SuppliersContainer)));
        // Lazily loaded: LoadAsync is async and DI is not. Blocking on .Result in a factory would starve
        // the worker's thread pool and make a Cosmos round-trip a precondition of starting the app.
        services.AddSingleton(sp => new SupplierCatalog(
            sp.GetRequiredService<ISupplierStore>(), opts.AllowlistPath,
            sp.GetRequiredService<ILogger<SupplierCatalog>>()));
        services.AddSingleton<MasterListRepo>();
        services.AddSingleton<RegistryRepo>();
        services.AddSingleton<Smx.Functions.Sds.Seeding.MasterListSeeder>();  // operator seed of the manifest

        // Bronze (ADLS)
        services.AddSingleton<IBronzeStore>(_ =>
            new AdlsBronzeStore(new DataLakeServiceClient(
                new Uri($"https://{opts.BronzeAccount}.dfs.core.windows.net"), cred)
                .GetFileSystemClient(opts.BronzeFilesystem)));

        // Ingestion deps
        services.AddSingleton(_ => new SdsValidator(opts.MinGhsSections));
        services.AddSingleton<GhsChunker>();
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IEmbedder>(_ => new Embedder(
            new AzureOpenAIClient(new Uri(opts.FoundryEndpoint), cred), opts.EmbeddingDeployment));
        services.AddSingleton<ISdsSearchClient>(_ => new SdsSearchClient(
            new SearchIndexClient(new Uri(opts.SearchEndpoint), cred, new SearchClientOptions
            {
                // camelCase so SdsChunk maps to the index field names (id, cas, contentVector, ...)
                Serializer = new JsonObjectSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            }),
            opts.SearchIndex));
        services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<IBronzeStore>(), sp.GetRequiredService<SdsValidator>(),
            sp.GetRequiredService<IPdfTextExtractor>(), sp.GetRequiredService<GhsChunker>(),
            sp.GetRequiredService<IEmbedder>(), sp.GetRequiredService<ISdsSearchClient>(),
            sp.GetRequiredService<RegistryRepo>(), opts));

        // The one acquisition path. SdsSweep, EnsureSds and RunSdsSync all go through it, which is what
        // makes "the timer did it" and "an agent asked for it" the same operation.
        services.AddSingleton<Smx.Functions.Sds.Acquisition.SdsAcquirer>();
        services.AddSingleton<Smx.Functions.Sds.Triggers.SdsSweep>();

        // Egress — real (NAT) or dry-run. Only SdsSweep consumes IEgressClient.
        if (opts.DryRun)
            services.AddSingleton<IEgressClient>(_ => DryRunEgressClient.Default(Array.Empty<byte>()));
        else
        {
            services.AddHttpClient();
            services.AddSingleton<IEgressClient>(sp => new NatEgressClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                opts,
                sp.GetRequiredService<ILogger<NatEgressClient>>()));
        }

        // ── Regulatory Sync (Reg/) — a separate subsystem in the same app, beside SDS. Reuses the shared
        //    credential, CosmosClient, IBronzeStore, and IEmbedder; adds its own Cosmos stores + Gold index. ──
        var regOpts = RegOptions.From(ctx.Configuration);
        services.AddSingleton(regOpts);

        services.AddSingleton<IRegStateStore>(sp => new CosmosRegStateStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(regOpts.CosmosDatabase, regOpts.StateContainer)));
        services.AddSingleton<IRegSilverStore>(sp => new CosmosRegSilverStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(regOpts.CosmosDatabase, regOpts.SilverContainer)));
        services.AddSingleton<IRegReviewStore>(sp => new CosmosRegReviewStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(regOpts.CosmosDatabase, regOpts.ReviewContainer)));
        services.AddSingleton<IRegRunsStore>(sp => new CosmosRegRunsStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(regOpts.CosmosDatabase, regOpts.RunsContainer)));
        services.AddSingleton<IRegRegistryStore>(sp => new CosmosRegRegistryStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(regOpts.CosmosDatabase, regOpts.RegistryContainer)));

        services.AddSingleton<IRegSearchClient>(_ => new RegSearchClient(
            new SearchIndexClient(new Uri(regOpts.SearchEndpoint), cred, new SearchClientOptions
            {
                Serializer = new JsonObjectSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            }),
            regOpts.SearchIndex));

        // Curated official-source registry + Bronze ingestor (reuses the shared IBronzeStore).
        services.AddSingleton(_ => RegRegistryProvider.FromFile(regOpts.RegistryPath));
        services.AddSingleton<BronzeIngestor>();

        // Parsers (one per source format) + registry that resolves them by RegSource.Parser.
        services.AddSingleton<IRegParser, OehhaProp65Parser>();
        services.AddSingleton<IRegParser, GenericCsvParser>();
        services.AddSingleton<IRegParser, EcfrParser>();
        // --- EurLex additions ---
        services.AddSingleton<IRegParser, EurLexHtmlParser>();
        // --- end EurLex ---
        services.AddSingleton<RegParserRegistry>();

        // The sync pipeline (testable core RunSyncAsync) — consumed by the RegSync timer + ReviewDecisionHttp.
        services.AddSingleton<SyncPipeline>();

        // One-time seed importer (local corpus → medallion, no egress) — consumed by the SeedImportHttp trigger.
        // Reuses the shared IBronzeStore + IEmbedder and the Reg Silver/State stores + Gold search client above.
        services.AddSingleton<Smx.Functions.Reg.Seeding.SeedImporter>();

        // Reg egress — its OWN allowlist (regulators), distinct type from the SDS IEgressClient.
        if (regOpts.DryRun)
            services.AddSingleton<IRegEgress>(_ => RegDryRunEgress.Default(Array.Empty<byte>()));
        else
        {
            services.AddHttpClient();
            services.AddSingleton<IRegEgress>(sp => new RegNatEgressClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<RegRegistryProvider>(), regOpts,
                sp.GetRequiredService<ILogger<RegNatEgressClient>>()));
        }
        // ---- Reference-data subsystem (seed the compatibility/supplier knowledge stores) ----
        var refOpts = ReferenceOptions.From(ctx.Configuration);
        services.AddSingleton(refOpts);
        services.AddSingleton<IReferenceStore>(sp =>
            new CosmosReferenceStore(sp.GetRequiredService<CosmosClient>(), refOpts.CosmosDatabase));
        services.AddSingleton<IReferenceSearchClient>(_ => new ReferenceSearchClient(
            new SearchIndexClient(new Uri(opts.SearchEndpoint), cred, new SearchClientOptions
            {
                Serializer = new JsonObjectSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            }),
            refOpts.SearchIndex));
        services.AddSingleton(sp => new ReferenceSeeder(
            sp.GetRequiredService<IReferenceStore>(),
            sp.GetRequiredService<IEmbedder>(),        // reused from the SDS registration
            sp.GetRequiredService<IReferenceSearchClient>(),
            refOpts));
    })
    .ConfigureLogging(logging =>
    {
        // The AI logger provider defaults to Warning+; drop that rule so the operational
        // Information logs ("SDS sweep: N due entries", candidate rejections) reach App Insights.
        logging.Services.Configure<LoggerFilterOptions>(options =>
        {
            var aiRule = options.Rules.FirstOrDefault(r =>
                r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (aiRule is not null) options.Rules.Remove(aiRule);
        });
    })
    .Build();

host.Run();
