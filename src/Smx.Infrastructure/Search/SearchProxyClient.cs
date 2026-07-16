using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Smx.Domain.Tools;
using Smx.SearchProxy.Contracts;

namespace Smx.Infrastructure.Search;

public interface ISearchProxyClient
{
    Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct);
}

/// Talks to the Search Proxy over its private endpoint, with an Entra token for the proxy's Easy Auth
/// audience. Note what is NOT sent: no project id, no correlation id, no client name — the request record
/// has no field for them, so this client could not send them if it tried.
///
/// DEPRECATED (2026-07-15): the client for the legacy proxy egress. Superseded by the hosted web-search tool
/// (WEB_SEARCH_PROVIDER=hosted); kept for revival (WEB_SEARCH_PROVIDER=proxy). The Search Proxy Function app
/// itself is untouched and can be redeployed independently.
[Obsolete("Client for the legacy anonymizing Search Proxy; superseded by the hosted web-search tool " +
          "(WEB_SEARCH_PROVIDER=hosted). Kept for revival via WEB_SEARCH_PROVIDER=proxy.", error: false)]
public sealed class SearchProxyClient(
    HttpClient http, TokenCredential credential, string endpoint, string audience, ILogger<SearchProxyClient> log)
    : ISearchProxyClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([$"{audience}/.default"]), ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/search")
            {
                Content = JsonContent.Create(new SearchRequest(query, intent, maxResults), options: Json),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(Json, ct);
                if (body is null) return new WebSearchResult([], "the search proxy returned an empty body");
                var hits = body.Results.Select(r => new WebHit(r.Title, r.Url, r.Snippet, r.Host)).ToList();
                return new WebSearchResult(hits, null);
            }

            // The proxy's error message is written FOR the model (SearchHttp.Explain) — relay it verbatim so
            // the agent learns what to do differently instead of just seeing nothing come back.
            var error = await resp.Content.ReadFromJsonAsync<SearchError>(Json, ct);
            log.LogWarning("Search proxy → {Status} {Reason}", (int)resp.StatusCode, error?.Reason);
            return new WebSearchResult([], error?.Message ?? $"the search proxy returned {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Search proxy call failed");
            return new WebSearchResult([], "the external search is unavailable — do NOT treat this as 'no results exist'");
        }
    }
}
