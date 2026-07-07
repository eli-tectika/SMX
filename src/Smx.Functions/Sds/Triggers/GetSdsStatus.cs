using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Sds.Triggers;

public sealed class GetSdsStatus
{
    private readonly MasterListRepo _repo;
    public GetSdsStatus(MasterListRepo repo) => _repo = repo;

    [Function("GetSdsStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sds/status/{element}/{form}")] HttpRequestData req,
        string element, string form)
    {
        var entry = await _repo.GetAsync(element, form, req.FunctionContext.CancellationToken);
        var resp = req.CreateResponse(entry is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        if (entry is not null) await resp.WriteAsJsonAsync(new { entry.Id, entry.Status, entry.AttemptCount });
        return resp;
    }
}
