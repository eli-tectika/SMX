using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
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

/// Deliberately NOT a SearchToolBase subclass, for two reasons:
///   • This index is ours end-to-end (LearnedConclusionsIndex builds it), so unlike the three
///     shared-schema corpora above it can be queried HYBRID — and it must be (see SearchAsync).
///   • A query against a missing index throws RequestFailedException (404), and that must degrade to
///     "no matches" for cold-start safety.
public sealed class LearnedConclusionsSearchTool(SearchClient client, IEmbedder embedder) : ILearnedConclusionsSearch
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        // A degenerate arg from the model. It must not reach the wire: an empty `search` is Azure's
        // match-ALL, so the agent would get back arbitrary prior conclusions it never asked for. Nor may
        // this return [] — that is ToolBox's "no prior conclusions on this" sentinel, and emitting it for a
        // question nobody asked is the same silent lie the hybrid query below exists to prevent. Throw: the
        // model sees a tool error and re-asks with a real query.
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query must be non-empty", nameof(query));

        // HYBRID (BM25 + vector), not keyword-only. A conclusion is recorded in the operator's words
        // ("barium overlaps the titanium K-beta line"); the agent asks in its own ("is Ba safe to tier for
        // an HDPE bottle?"). Term overlap between the two is near zero, so a BM25-only query returns nothing
        // and this tool reports "no prior conclusions" — indistinguishable from a genuinely empty knowledge
        // layer, silently switching off the entire "gets smarter" loop. The vector half is what bridges the
        // operator's vocabulary to the agent's.
        //
        // OUTSIDE the try, deliberately: the 404 catch must scope only the SEARCH. An embeddings-side 403 or
        // 500 swallowed as "no prior conclusions" would let an agent reason as though no prior evidence
        // existed, which is the exact failure the catch below is narrowed to avoid. It costs one embedding
        // call on a cold start (before the 404) — a bounded, sub-cent price for keeping the two failure
        // domains apart; the alternative (an index-existence probe) would put a control-plane round-trip on
        // every warm call to save one cold one.
        var vectors = await embedder.EmbedAsync([query], ct);
        if (vectors.Count == 0)
            throw new InvalidOperationException("IEmbedder returned no vector for the query — cannot run a hybrid search");

        var options = new SearchOptions
        {
            Size = top,
            VectorSearch = new VectorSearchOptions
            {
                // "contentVector" MUST match the vector field in LearnedConclusionsIndex.BuildIndex.
                Queries = { new VectorizedQuery(vectors[0]) { KNearestNeighborsCount = top, Fields = { "contentVector" } } },
            },
        };
        try
        {
            var response = await client.SearchAsync<Dictionary<string, object>>(query, options, ct);
            var results = new List<RetrievedChunk>();
            await foreach (var r in response.Value.GetResultsAsync())
            {
                var doc = r.Document;
                var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
                var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                // NB: with a hybrid query this Score is an RRF score (~0.01–0.03), not BM25 — it is not
                // comparable with the scores the other three tools return. Nothing in the codebase
                // thresholds or ranks across tools, so this is safe today; keep it that way.
                results.Add(new RetrievedChunk("learned-conclusions", $"{client.IndexName}/{id}", content, r.Score ?? 0));
            }
            return results;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // The index does not exist until the first conclusion is pushed (LearnedConclusionsIndex creates
            // it). Cold start is NOT an error: an agent must be able to run on day one. Only 404 is swallowed
            // — a 403 or 500 must still throw, or a silenced auth failure would let an agent reason as though
            // no prior evidence existed.
            return [];
        }
    }
}
