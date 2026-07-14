using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;

// The anonymizing Search Proxy. This app is deployed to a Function App whose managed identity holds ZERO
// corpus RBAC — it can reach the internet and its own storage, and nothing else. Keep it that way.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) => ProxyHost.ConfigureServices(services, ctx.Configuration))
    .Build();

// Resolving it is what emits it: a registration nothing ever resolves is a warning nobody ever reads.
host.Services.GetService<IStartupWarning>();

host.Run();

namespace Smx.SearchProxy
{
    /// Extracted from the top-level statements so HostWiringTests can build the real graph and catch a
    /// missing registration at test time rather than at 3am (the OrchestratorHostWiringTests pattern).
    public static class ProxyHost
    {
        public static void ConfigureServices(IServiceCollection services, IConfiguration config)
        {
            var opts = ProxyOptions.From(config);
            services.AddSingleton(opts);

            // A cover count below 2 would let a real query egress alone, unhidden. ProxyOptions clamps it —
            // and we say so out loud, so the misconfiguration is visible instead of silently corrected.
            if (opts.CoverCountRaw != opts.CoverCount)
                services.AddSingleton<IStartupWarning>(sp => new StartupWarning(
                    sp.GetRequiredService<ILogger<StartupWarning>>(),
                    $"PROXY_COVER_COUNT={opts.CoverCountRaw} was clamped to {opts.CoverCount}: a real query may never egress alone."));

            // Loaded EAGERLY, not from a factory: a corpus that is missing, unparseable or too thin to fill a
            // cover batch must fail the deploy, not the first live query at 3am.
            var corpus = CoverCorpus.FromFile(opts.CoverCorpusPath);
            EnsureCorpusCanFillTheBatch(corpus, opts.CoverCount);
            services.AddSingleton(corpus);

            services.AddSingleton<IShuffler, RandomShuffler>();
            services.AddSingleton<CoverBatch>();
            services.AddSingleton<StructuralGuard>();
            services.AddSingleton<EgressAudit>();

            if (opts.DryRun)
            {
                // No key, no storage, no network — the whole pipeline still runs.
                services.AddSingleton<ISearchProvider>(_ => new DryRunSearchProvider());
                services.AddSingleton<ISearchCache>(_ => new NullSearchCache());
                services.AddSingleton<IQuotaStore>(_ => new NullQuotaStore());
            }
            else
            {
                services.AddHttpClient<ISearchProvider, BraveSearchProvider>()
                    // The handler is built by ProxyHttp so the traceparent suppression is testable against the
                    // real chain — see ProxyHttp and TracePropagationTests. Do not inline it back here.
                    .ConfigurePrimaryHttpMessageHandler(ProxyHttp.CreateHandler);

                TokenCredential cred = string.IsNullOrEmpty(opts.UamiClientId)
                    ? new DefaultAzureCredential()
                    : new ManagedIdentityCredential(opts.UamiClientId);
                services.AddSingleton(cred);

                var blobUri = new Uri($"https://{opts.StorageAccount}.blob.core.windows.net");
                services.AddSingleton(_ => new BlobServiceClient(blobUri, cred).GetBlobContainerClient(opts.CacheContainer));
                services.AddSingleton<ISearchCache, BlobSearchCache>();
                services.AddSingleton<IQuotaStore, BlobQuotaStore>();
            }

            services.AddSingleton<QuotaGuard>();
            services.AddSingleton<SearchPipeline>();
        }

        /// CoverBatch fills a batch with `Take(CoverCount - 1)` decoys — and Take() UNDER-FILLS silently when
        /// the family holds fewer. Raise PROXY_COVER_COUNT past the size of the thinnest family and the
        /// anonymity set would quietly shrink, with every test still green and no signal anywhere: the one
        /// property this component exists to provide, degraded in silence. So we cross-check the two at
        /// startup and refuse to boot. A deploy that fails loudly is the cheapest possible failure here.
        ///
        /// The requirement is CoverCount, not CoverCount - 1: CoverBatch first drops any decoy equal to the
        /// real query, so a family of exactly CoverCount - 1 could still come up one short.
        public static void EnsureCorpusCanFillTheBatch(CoverCorpus corpus, int coverCount)
        {
            foreach (var intent in SearchIntents.All)
            {
                var have = corpus.For(intent).Count;
                if (have < coverCount)
                    throw new InvalidOperationException(
                        $"cover corpus family '{intent}' holds {have} decoys, but PROXY_COVER_COUNT={coverCount} " +
                        $"needs {coverCount} (a batch draws {coverCount - 1}, and one may be dropped as a duplicate of " +
                        $"the real query) — the real query would egress in a batch smaller than configured, silently " +
                        $"shrinking the anonymity set. Add decoys or lower PROXY_COVER_COUNT.");
            }
        }
    }

    public interface IStartupWarning;

    public sealed class StartupWarning : IStartupWarning
    {
        public StartupWarning(ILogger<StartupWarning> log, string message) => log.LogWarning("{Message}", message);
    }
}
