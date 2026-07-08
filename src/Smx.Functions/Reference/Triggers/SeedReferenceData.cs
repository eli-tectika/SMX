// src/Smx.Functions/Reference/Triggers/SeedReferenceData.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Reference.Config;
using Smx.Functions.Reference.Seeding;

namespace Smx.Functions.Reference.Triggers;

public sealed class SeedReferenceData
{
    private readonly ReferenceSeeder _seeder;
    private readonly ReferenceOptions _opts;
    public SeedReferenceData(ReferenceSeeder seeder, ReferenceOptions opts)
    { _seeder = seeder; _opts = opts; }

    [Function("SeedReferenceData")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reference/seed")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;
        var dir = Path.IsPathRooted(_opts.SeedPath)
            ? _opts.SeedPath
            : Path.Combine(AppContext.BaseDirectory, _opts.SeedPath);

        var data = await SeedDataLoader.LoadAsync(dir, ct);
        var report = await _seeder.SeedAsync(data, ct);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(report);
        return resp;
    }
}
