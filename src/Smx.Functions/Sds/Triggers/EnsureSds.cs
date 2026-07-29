using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Acquisition;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

public sealed record EnsureRequest(string Cas, string? Element, string? Form, bool Force = false);

/// On-demand acquisition — the door an agent and the operator's "Fetch now" button both come through.
///
/// Synchronous by design. The point of the 2026-07-29 redesign is that an actor discovering a missing
/// sheet gets an answer in the same breath, instead of parking a stage and waiting for a cron.
public sealed class EnsureSds
{
    // Matches MasterListSeeder's CAS shape, so the two ends of the system agree on what a CAS is.
    private static readonly Regex CasPattern = new(@"^\d{2,7}-\d{2}-\d$", RegexOptions.Compiled);

    private readonly SdsAcquirer _acquirer;
    public EnsureSds(SdsAcquirer acquirer) => _acquirer = acquirer;

    [Function("EnsureSds")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/ensure")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;

        EnsureRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<EnsureRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException) { body = null; }

        var cas = body?.Cas?.Trim() ?? "";
        if (!CasPattern.IsMatch(cas))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = $"'{cas}' is not a CAS number" }, ct);
            return bad;
        }

        var key = new SubstanceKey(body!.Element?.Trim() ?? "", body.Form?.Trim() ?? "", cas);
        var result = await _acquirer.EnsureAsync(key, body.Force, DateTimeOffset.UtcNow.ToString("O"), ct);

        // 200 even when the sheet could not be had. "We tried and could not get it" is a successful
        // answer to a question, and a 5xx would make every such call look to the caller like an outage —
        // which is exactly the distinction `attempted` exists to preserve.
        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(result, ct);
        return resp;
    }
}
