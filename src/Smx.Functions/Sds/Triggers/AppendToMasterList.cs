using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

public sealed record AppendRequest(string Element, string Form, string Cas, string? SubstrateClass);

public sealed class AppendToMasterList
{
    private readonly MasterListRepo _repo;
    public AppendToMasterList(MasterListRepo repo) => _repo = repo;

    [Function("AppendToMasterList")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/master-list")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<AppendRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body is null || string.IsNullOrWhiteSpace(body.Element) || string.IsNullOrWhiteSpace(body.Form))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var added = await _repo.AppendAsync(body.Element, body.Form, body.Cas, body.SubstrateClass,
            "agent", DateTimeOffset.UtcNow.ToString("O"), req.FunctionContext.CancellationToken);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new { added, id = DedupKey.ForMasterList(body.Element, body.Form) });
        return resp;
    }
}
