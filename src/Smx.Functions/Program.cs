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
    })
    .Build();

host.Run();
