using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Triggers;

/// Anonymous here; platform Easy Auth / Entra is enforced at the infra layer (functions.bicep
/// `searchProxyAuth`), and public inbound is disabled by harden.sh — this app is reached only over its
/// private endpoint, by the orchestrator, with an Entra token.
public sealed class SearchHttp(SearchPipeline pipeline, ILogger<SearchHttp> log)
{
    /// Invariant 3. UnmappedMemberHandling.Disallow is what makes "project-blind" enforceable rather than
    /// aspirational: a body carrying projectId / client / url is a 400, not a silently-ignored field.
    public static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Function("Search")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "search")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;

        SearchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SearchRequest>(req.Body, StrictJson, ct);
        }
        catch (JsonException ex)
        {
            log.LogWarning("SearchProxyAudit decision={Decision} reason={Reason} error={Error}",
                "blocked", "malformed_or_unknown_field", ex.Message);
            return await Error(req, HttpStatusCode.BadRequest,
                new SearchError("malformed_or_unknown_field", Explain("malformed_or_unknown_field")));
        }
        if (body is null)
        {
            // Invariant 6: every request is audited — including the ones rejected before the pipeline runs.
            log.LogWarning("SearchProxyAudit decision={Decision} reason={Reason}", "blocked", "empty_body");
            return await Error(req, HttpStatusCode.BadRequest, new SearchError("empty_body", Explain("empty_body")));
        }

        var result = await ExecuteAsync(body, DateTimeOffset.UtcNow.ToString("O"), ct);

        if (result.Response is not null)
        {
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result.Response, ct);
            return ok;
        }
        return await Error(req, (HttpStatusCode)result.StatusCode,
            new SearchError(result.Reason ?? "error", Explain(result.Reason)));
    }

    /// The testable core (house convention: the trigger is a shell, `nowUtc` is a parameter).
    ///
    /// The quota store fails CLOSED — when it cannot confirm the spend it throws rather than let an
    /// unmetered batch egress. Un-caught, that would surface as a 500: "the proxy is broken". It is not
    /// broken; it is refusing to spend a budget it cannot count. That is a 429, and the agent is told to
    /// fall back to the catalog rather than to retry into a store that is already contended.
    public async Task<PipelineResult> ExecuteAsync(SearchRequest body, string nowUtc, CancellationToken ct)
    {
        try
        {
            return await pipeline.RunAsync(body, nowUtc, ct);
        }
        catch (QuotaUnavailableException ex)
        {
            log.LogError(ex, "SearchProxyAudit decision={Decision} intent={Intent} reason={Reason}",
                "blocked", body.Intent, "quota_unavailable");
            return new PipelineResult(null, 429, "quota_unavailable");
        }
    }

    /// The message is written for the MODEL, not for a human: it is relayed verbatim into the tool result, so
    /// it must tell the agent what to do differently.
    public static string Explain(string? reason) => reason switch
    {
        "malformed_or_unknown_field" =>
            "The request carried a field that is not part of the contract, or was not valid JSON. " +
            "The Search Proxy is project-blind by design: it accepts only query, intent, maxResults, freshnessDays.",
        "empty_body" => "A JSON body is required.",
        "query_empty" => "The query was empty. Ask a specific chemical question.",
        "query_too_long" => "The query was too long. Shorten it to a focused chemical question.",
        "unknown_intent" => "Unknown intent. Use discovery.candidate_forms, discovery.form_properties or discovery.supplier_availability.",
        // Deliberately does NOT quote the ceiling: it is the operator's (PROXY_MAX_RESULTS), so a hardcoded
        // "between 1 and 20" would tell the model to retry with a number that is still refused — a loop.
        "max_results_out_of_range" =>
            "maxResults was outside the range this proxy accepts. Ask for fewer results; 10 is a sensible default.",
        "contains_guid" or "contains_email" or "contains_url" or "contains_digit_run" =>
            "The query contained an identifier (an id, an address, a URL or a long number). Rephrase it in generic chemical terms — " +
            "the external search must never carry anything that identifies this project.",
        "quota_exceeded" => "The external-search budget is exhausted. Continue from the catalog and the reference corpus.",
        "quota_unavailable" => "The external-search budget could not be confirmed. Continue from the catalog and the reference corpus.",
        "provider_failed" => "The external search did not answer. Do NOT treat this as 'no results exist' — it is not evidence of absence.",
        "provider_not_configured" => "External search is not configured. Continue from the catalog and the reference corpus.",
        _ => "The external search could not be completed.",
    };

    private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, SearchError error)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteAsJsonAsync(error, req.FunctionContext.CancellationToken);
        return resp;
    }
}
