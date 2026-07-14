using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Config;

// The anonymizing Search Proxy. This app is deployed to a Function App whose managed identity holds ZERO
// corpus RBAC — it can reach the internet and its own storage, and nothing else. Keep it that way.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(ProxyOptions.From(ctx.Configuration));
    })
    .Build();

// A cover count below 2 would let a real query egress alone, unhidden — ProxyOptions clamps it, and we say
// so out loud, so the misconfiguration is visible in the logs instead of being silently corrected.
var opts = host.Services.GetRequiredService<ProxyOptions>();
if (opts.CoverCountRaw != opts.CoverCount)
    host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Smx.SearchProxy")
        .LogWarning(
            "PROXY_COVER_COUNT={Raw} is below the k-anonymity minimum and was clamped to {Clamped}.",
            opts.CoverCountRaw, opts.CoverCount);

host.Run();
