using Azure;
using Azure.Search.Documents;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// One class per index so DI can register each interface against its own SearchClient.
public abstract class SearchToolBase(SearchClient client, string sourceName)
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        var options = new SearchOptions { Size = top };
        // Text search is the lowest common denominator across the three index schemas; hybrid/vector
        // upgrades happen per-index once schemas are unified (regulatory schema arrives from the team).
        var response = await client.SearchAsync<Dictionary<string, object>>(query, options, ct);
        var results = new List<RetrievedChunk>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var doc = r.Document;
            var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
            var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
            results.Add(new RetrievedChunk(sourceName, $"{client.IndexName}/{id}", content, r.Score ?? 0));
        }
        return results;
    }
}

public sealed class RegulatorySearchTool(SearchClient client) : SearchToolBase(client, "regulatory"), IRegulatorySearch;
public sealed class SdsSearchTool(SearchClient client) : SearchToolBase(client, "sds"), ISdsSearch;
public sealed class ReferenceSearchTool(SearchClient client) : SearchToolBase(client, "reference"), IReferenceSearch;

/// The `learned-conclusions` index has no writer until Plan 3b, so until then it does not exist.
/// A query against a missing index throws RequestFailedException (404); this must degrade to
/// "no matches" for cold-start safety, so this tool is standalone rather than a SearchToolBase
/// subclass, wrapping the query in a try/catch for that one case.
public sealed class LearnedConclusionsSearchTool(SearchClient client) : ILearnedConclusionsSearch
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        try
        {
            var response = await client.SearchAsync<Dictionary<string, object>>(query, new SearchOptions { Size = top }, ct);
            var results = new List<RetrievedChunk>();
            await foreach (var r in response.Value.GetResultsAsync())
            {
                var doc = r.Document;
                var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
                var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                results.Add(new RetrievedChunk("learned-conclusions", $"{client.IndexName}/{id}", content, r.Score ?? 0));
            }
            return results;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // Index not created yet (no conclusions written — Plan 3b creates it on first push). Cold-start → no matches.
            return [];
        }
    }
}
