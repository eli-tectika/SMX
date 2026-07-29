using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

/// Adding a supplier without a PR and a redeploy. This endpoint IS the gate removal — the Cosmos
/// container behind it buys nothing if the only way to write a row is a deployment.
public sealed class UpsertSupplier
{
    private readonly ISupplierStore _store;
    private readonly SupplierCatalog _catalog;

    public UpsertSupplier(ISupplierStore store, SupplierCatalog catalog)
    { _store = store; _catalog = catalog; }

    [Function("UpsertSupplier")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/suppliers")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;

        AllowlistEntry? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AllowlistEntry>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        // A row with no domain has no identity and a row with no strategy can never be dispatched;
        // either would sit in the container looking like coverage and producing none.
        if (body is null || string.IsNullOrWhiteSpace(body.Domain) || string.IsNullOrWhiteSpace(body.Strategy))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        await _store.UpsertAsync(body, ct);
        _catalog.Invalidate();   // the next sweep sees it; no restart

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new { id = body.Id, domain = body.Id, strategy = body.Strategy }, ct);
        return resp;
    }
}
