using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Seeding;

namespace Smx.Functions.Reg.Triggers;

// One-time seed import trigger — POST /reg/seed. Loads the local pre-collected corpus (RegOptions.SeedPath) into
// the medallion with no network egress, then returns the SeedReport as JSON. Mirrors the SDS OperatorUpload HTTP
// pattern (Anonymous here; platform Easy Auth / Entra is enforced at the infra layer). Idempotent — safe to
// re-invoke (deterministic ids merge rather than duplicate).
public sealed class SeedImportHttp
{
    private readonly SeedImporter _importer;
    private readonly RegOptions _opts;
    private readonly ILogger<SeedImportHttp> _log;

    public SeedImportHttp(SeedImporter importer, RegOptions opts, ILogger<SeedImportHttp> log)
    { _importer = importer; _opts = opts; _log = log; }

    [Function("RegSeedImport")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reg/seed")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;
        _log.LogInformation("Reg seed import starting from {Root}", _opts.SeedPath);
        var report = await _importer.ImportAsync(_opts.SeedPath, ct);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(report, ct);
        return resp;
    }
}
