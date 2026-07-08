using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Files.DataLake;
using Microsoft.Azure.Cosmos;
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

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var opts = SdsOptions.From(ctx.Configuration);
        services.AddSingleton(opts);

        TokenCredential cred = string.IsNullOrEmpty(opts.UamiClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(opts.UamiClientId);
        services.AddSingleton(cred);

        // Allowlist (single artifact) + strategies + resolver
        services.AddSingleton(_ => AllowlistProvider.FromFile(opts.AllowlistPath));
        services.AddSingleton<ISourceStrategy, CasTemplateStrategy>();
        services.AddSingleton<ISourceStrategy, ProductLookupStrategy>();
        services.AddSingleton<SourceResolver>();

        // Cosmos (camelCase so records map to /element, /cas partition keys + registry field queries)
        services.AddSingleton(_ => new CosmosClient(opts.CosmosEndpoint, cred, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }
        }));
        services.AddSingleton<IMasterListStore>(sp => new CosmosMasterListStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.MasterContainer)));
        services.AddSingleton<IRegistryStore>(sp => new CosmosRegistryStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RegistryContainer)));
        services.AddSingleton<MasterListRepo>();
        services.AddSingleton<RegistryRepo>();

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
            sp.GetRequiredService<RegistryRepo>(),
            sp.GetRequiredService<AllowlistProvider>().Domains, opts));

        // Egress — real (NAT) or dry-run. Only SdsSweep consumes IEgressClient.
        if (opts.DryRun)
            services.AddSingleton<IEgressClient>(_ => DryRunEgressClient.Default(Array.Empty<byte>()));
        else
        {
            services.AddHttpClient();
            services.AddSingleton<IEgressClient>(sp => new NatEgressClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<AllowlistProvider>(), opts,
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
    })
    .Build();

host.Run();
