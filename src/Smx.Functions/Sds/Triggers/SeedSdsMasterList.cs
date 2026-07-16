using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Seeding;

namespace Smx.Functions.Sds.Triggers;

// Operator-triggered, idempotent seeding of the SDS master list from the bundled reference catalog.
// Mirrors the SeedReferenceData / RegSeedImport pattern. Seeds the MANIFEST only — never fetches.
public sealed class SeedSdsMasterList
{
    private readonly MasterListSeeder _seeder;
    private readonly SdsOptions _opts;
    public SeedSdsMasterList(MasterListSeeder seeder, SdsOptions opts)
    { _seeder = seeder; _opts = opts; }

    [Function("SeedSdsMasterList")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/seed")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;
        var path = Path.IsPathRooted(_opts.SeedCatalogPath)
            ? _opts.SeedCatalogPath
            : Path.Combine(AppContext.BaseDirectory, _opts.SeedCatalogPath);

        if (!File.Exists(path))
        {
            var nf = req.CreateResponse(HttpStatusCode.InternalServerError);
            await nf.WriteAsJsonAsync(new { error = $"seed catalog not found at '{_opts.SeedCatalogPath}'" });
            return nf;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var report = await _seeder.SeedAsync(json, DateTimeOffset.UtcNow.ToString("O"), ct);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(report);
        return resp;
    }
}
