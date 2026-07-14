using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Triggers;

/// Anonymous here; platform Easy Auth / Entra is enforced at the infra layer (functions.bicep
/// `searchProxyAuth`), and public inbound is disabled by harden.sh — this app is reached only over its
/// private endpoint, by the orchestrator, with an Entra token.
public sealed class HealthHttp(ProxyOptions opts)
{
    /// Reports readiness WITHOUT leaking the key or any query. `configured` is what an operator actually
    /// needs to know: whether this proxy can currently answer. It is a boolean derived from the key, never
    /// the key itself, never a prefix of it, and never anything a caller has asked for.
    [Function("Health")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new
        {
            status = "ok",
            provider = opts.Provider,
            dryRun = opts.DryRun,
            configured = opts.DryRun || !string.IsNullOrEmpty(opts.ApiKey),
            coverCount = opts.CoverCount,
        }, req.FunctionContext.CancellationToken);
        return resp;
    }
}
