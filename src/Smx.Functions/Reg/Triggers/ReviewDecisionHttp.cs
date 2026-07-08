using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;

namespace Smx.Functions.Reg.Triggers;

// The corpus review-gate resume — active ONLY on the anomaly path. The SMX app surfaces the held run's diff,
// the operator records the R.E.'s determination, and the app calls this endpoint. Mirrors the SDS OperatorUpload
// HTTP pattern (Anonymous here; platform Easy Auth / Entra is enforced at the infra layer). Approve → promote to
// Gold; reject → discard the staged Silver. Guards: the run must exist and be in the `held` state.
public sealed class ReviewDecisionHttp
{
    private readonly IRegReviewStore _review;
    private readonly IRegSilverStore _silver;
    private readonly SyncPipeline _pipeline;
    private readonly ILogger<ReviewDecisionHttp> _log;

    public ReviewDecisionHttp(IRegReviewStore review, IRegSilverStore silver, SyncPipeline pipeline, ILogger<ReviewDecisionHttp> log)
    { _review = review; _silver = silver; _pipeline = pipeline; _log = log; }

    [Function("RegReviewDecision")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reg/review/{runId}")] HttpRequestData req,
        string runId)
    {
        var ct = req.FunctionContext.CancellationToken;
        var body = await JsonSerializer.DeserializeAsync<ReviewDecisionRequest>(
            req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Decision) || string.IsNullOrWhiteSpace(body.SignoffBy))
            return await Text(req, HttpStatusCode.BadRequest, "decision and signoffBy are required");

        var record = await _review.GetAsync(runId, ct);
        if (record is null)
            return await Text(req, HttpStatusCode.NotFound, $"no review record for {runId}");
        if (record.Status != RegStatus.Held)
            return await Text(req, HttpStatusCode.Conflict, $"run {runId} is '{record.Status}', not '{RegStatus.Held}'");

        var decision = body.Decision.Trim().ToLowerInvariant();
        var signoff = new OperatorSignoff(body.SignoffBy, DateTimeOffset.UtcNow.ToString("O"), body.Reason);

        switch (decision)
        {
            case "approve":
                await _pipeline.PromoteAsync(runId, ct);
                await _review.UpsertAsync(record with { Status = RegStatus.Approved, DecisionKind = "human", Signoff = signoff }, ct);
                _log.LogInformation("Reg review {RunId} APPROVED by {By}", runId, body.SignoffBy);
                return await Text(req, HttpStatusCode.OK, "approved and promoted");

            case "reject":
                await _silver.DiscardStagedAsync(runId, ct);
                await _review.UpsertAsync(record with { Status = RegStatus.Rejected, DecisionKind = "human", Signoff = signoff }, ct);
                _log.LogInformation("Reg review {RunId} REJECTED by {By}", runId, body.SignoffBy);
                return await Text(req, HttpStatusCode.OK, "rejected and discarded");

            default:
                return await Text(req, HttpStatusCode.BadRequest, "decision must be 'approve' or 'reject'");
        }
    }

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode code, string msg)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteStringAsync(msg);
        return resp;
    }
}
