using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Smx.Domain.Records;

namespace Smx.Domain;

/// One Learned Conclusion as an AI Search document. The wire names are pinned to the type with
/// [JsonPropertyName] — deliberately, not left to DI: nothing in Smx.Backend.sln registers a camelCase
/// serializer (Smx.Orchestrator builds bare SearchClients with default options), so a PascalCase payload
/// would miss the index schema and the reader's `doc["content"]` lookup would return "" for every hit.
/// These names must match the schema built by LearnedConclusionsIndex.EnsureIndexAsync exactly.
public sealed record LearnedConclusionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("contentVector")] float[] ContentVector,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("element")] string? Element,
    [property: JsonPropertyName("form")] string? Form,
    [property: JsonPropertyName("material")] string? Material,
    [property: JsonPropertyName("application")] string? Application,
    [property: JsonPropertyName("market")] string? Market,
    [property: JsonPropertyName("substance")] string? Substance,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

/// Cosmos doc → index document. Cosmos is authoritative; this is the retrievable projection of it.
public static class LearnedConclusionProjection
{
    /// The searchable text — and the ONLY thing a retrieving agent ever sees.
    ///
    /// LearnedConclusionsSearchTool maps a hit to RetrievedChunk(source, "index/{id}", content, score):
    /// it reads `id` and `content` and nothing else. So every fact the agent must weigh has to live in
    /// this string — the scope terms (so the agent can tell whether the conclusion even applies), the
    /// confidence and the timestamp (the agent instructions say a higher-confidence, more recent
    /// conclusion supersedes an older one — it cannot apply that rule to numbers it cannot see), the
    /// source projects, and the operator's verbatim reason. A filterable sibling field the reader never
    /// selects is dead weight for retrieval; it exists only for future filtered queries.
    ///
    /// Corollary: LearnedConclusionDoc.Supersedes is deferred in Plan 3b (nothing writes it, it stays null).
    /// When Plan 5 populates it, THIS METHOD must be updated in the same change, or the supersession is
    /// invisible to every reader forever.
    public static string Content(LearnedConclusionDoc d)
    {
        var scope = new[] { d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var lines = new List<string>
        {
            $"[{d.Kind}] {string.Join(" · ", scope)}".TrimEnd(),
            d.Finding,
            $"confidence: {d.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} · recorded: {d.CreatedAt}",
        };
        if (d.Provenance.SourceProjects.Count > 0)
            lines.Add($"source projects: {string.Join(", ", d.Provenance.SourceProjects)}");
        if (d.Provenance.Decisions.Count > 0)
            lines.Add($"decisions: {string.Join(" | ", d.Provenance.Decisions)}");
        return string.Join("\n", lines);
    }

    /// Azure AI Search document keys may contain only letters, digits, '_', '-' and '='. A conclusion's
    /// Cosmos id is pipe-delimited ("material|proj-1|revision|discovery|aaaa1111"), so pushing it as the
    /// key would have every document REJECTED — silently, since nothing inspects IndexDocumentsResult.
    /// The index would stay empty and search_learned_conclusions would answer "no matches" forever.
    ///
    /// Deterministic (slug + a hash of the raw id), so change-feed redelivery of the same revision still
    /// upserts one document rather than accumulating duplicates — the property KnowledgeIds.RevisionConclusion
    /// was designed for must survive this mapping. Twin of ReferenceKey.SearchKey in Smx.Functions, which
    /// lives in another solution and cannot be referenced from here.
    public static string SearchKey(string cosmosId)
    {
        var slug = Regex.Replace(cosmosId.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 128) slug = slug[..128].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cosmosId))).ToLowerInvariant()[..10];
        return slug.Length == 0 ? hash : $"{slug}-{hash}";
    }

    public static LearnedConclusionChunk ToChunk(LearnedConclusionDoc d, float[] vector) => new(
        SearchKey(d.Id), Content(d), vector, d.Kind,
        d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance,
        d.Confidence, d.CreatedAt);
}
