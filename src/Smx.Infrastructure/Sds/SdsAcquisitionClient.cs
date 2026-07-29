using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Sds;

/// Talks to the `regsync` Function App over its private endpoint, with an Entra token for its Easy Auth
/// audience. Deliberately the same shape as SearchProxyClient — same token acquisition, same audience
/// handling, same "a failure is a result, not an exception" contract.
///
/// Acquisition lives in `regsync` rather than here because that app sits on the only subnet with a NAT
/// gateway (the ACA subnet the backend runs in has no internet path at all) and already holds the Bronze,
/// AI Search and Cosmos write grants. See D6 in the 2026-07-29 design.
///
/// Note what is NOT sent: no project id, no client name. The request is keyed by substance, and there is
/// no field on it a project identity could travel in.
public sealed class SdsAcquisitionClient(
    HttpClient http, TokenCredential credential, string endpoint, string audience,
    ILogger<SdsAcquisitionClient> log) : ISdsAcquisition
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record EnsureRequest(string Cas, string? Element, string? Form, bool Force = false);
    private sealed record AppendRequest(string Element, string Form, string Cas);

    public async Task<SdsEnsureResult> EnsureAsync(string cas, string? element, string? form, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync("sds/ensure", new EnsureRequest(cas, element, form), ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("SDS ensure → {Status} for CAS {Cas}: {Detail}", (int)resp.StatusCode, cas, detail);
                return new SdsEnsureResult(SdsEnsureStatus.Unavailable,
                    Reason: $"the SDS service returned {(int)resp.StatusCode}");
            }

            return await resp.Content.ReadFromJsonAsync<SdsEnsureResult>(Json, ct)
                   ?? new SdsEnsureResult(SdsEnsureStatus.Unavailable,
                       Reason: "the SDS service returned an empty body");
        }
        // Cancellation means the caller went away. Swallowing it would turn an abandoned turn into a
        // fabricated "we tried and could not get it" — a claim about the world that was never tested.
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "SDS ensure failed for CAS {Cas}", cas);
            return new SdsEnsureResult(SdsEnsureStatus.Unavailable,
                Reason: $"the SDS service is unreachable ({ex.Message}) — this is NOT evidence that no sheet exists");
        }
    }

    /// Bookkeeping, so it swallows everything except cancellation: a ledger append exists to make the
    /// scheduled sweep aware of a substance, and a stage must never fail because that record-keeping did.
    public async Task AppendAsync(string element, string form, string cas, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync("sds/master-list", new AppendRequest(element, form, cas), ct);
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("SDS ledger append → {Status} for {Element}/{Form}", (int)resp.StatusCode, element, form);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "SDS ledger append failed for {Element}/{Form} ({Cas})", element, form, cas);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string route, object body, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([$"{audience}/.default"]), ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/{route}")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        return await http.SendAsync(req, ct);
    }
}
