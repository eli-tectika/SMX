using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Sds.Triggers;

public sealed class GetSdsForSubstance
{
    private readonly RegistryRepo _repo;
    public GetSdsForSubstance(RegistryRepo repo) => _repo = repo;

    [Function("GetSdsForSubstance")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sds/substance")] HttpRequestData req)
    {
        var pointer = await _repo.GetForSubstanceAsync(Query(req.Url, "cas"), Query(req.Url, "productName"),
            req.FunctionContext.CancellationToken);

        if (pointer is null)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteAsJsonAsync(new { present = false });
            return nf;
        }
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { present = true, pointer.Id, pointer.BlobPath, pointer.IndexDocIds, pointer.RevisionDate });
        return ok;
    }

    // Dependency-free query parsing (no System.Web).
    private static string? Query(Uri url, string key)
    {
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (Uri.UnescapeDataString(kv[0]) == key) return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return null;
    }
}
