using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Triggers;

public sealed record OperatorUploadRequest(
    string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string MasterListId, string PdfBase64);

public sealed class OperatorUpload
{
    private readonly IngestionPipeline _pipeline;
    private readonly AllowlistProvider _allowlist;
    public OperatorUpload(IngestionPipeline pipeline, AllowlistProvider allowlist)
    { _pipeline = pipeline; _allowlist = allowlist; }

    [Function("OperatorUpload")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/upload")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<OperatorUploadRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body is null || string.IsNullOrWhiteSpace(body.Cas) || string.IsNullOrWhiteSpace(body.PdfBase64))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var pdf = Convert.FromBase64String(body.PdfBase64);
        // Operator-supplied source: validate against the supplier's allowlist domain if known, else the
        // first (highest-priority) allowlist domain — the operator pulled this SDS during the offline audit.
        var domain = _allowlist.Ordered.FirstOrDefault(e =>
            string.Equals(e.Supplier, body.Supplier, StringComparison.OrdinalIgnoreCase))?.Domain
            ?? _allowlist.Ordered[0].Domain;

        var meta = new SdsMetadata(body.Cas, body.Supplier, body.ProductName, body.RevisionDate,
            body.Region, body.Language, $"operator-upload://{body.Supplier}", body.MasterListId);
        var result = await _pipeline.IngestAsync(pdf, meta, domain, req.FunctionContext.CancellationToken);

        var resp = req.CreateResponse(result.Ok ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity);
        await resp.WriteAsJsonAsync(new { result.Ok, result.Reason, result.RegistryId });
        return resp;
    }
}
