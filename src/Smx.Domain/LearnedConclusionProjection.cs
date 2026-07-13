using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain;

/// One Learned Conclusion as an AI Search document. Field names here become index field names (the
/// SearchIndexClient is registered with a camelCase serializer), so they must match the schema built by
/// LearnedConclusionsIndex.EnsureIndexAsync exactly.
public sealed record LearnedConclusionChunk(
    string Id, string Content, float[] ContentVector, string Kind,
    string? Element, string? Form, string? Material, string? Application, string? Market, string? Substance,
    double Confidence, string CreatedAt);

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
    public static string Content(LearnedConclusionDoc d)
    {
        var scope = new[] { d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var lines = new List<string>
        {
            $"[{d.Kind}] {string.Join(" · ", scope)}".TrimEnd(),
            d.Finding,
            $"confidence: {d.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} · recorded: {d.CreatedAt}",
            $"source projects: {string.Join(", ", d.Provenance.SourceProjects)}",
        };
        if (d.Provenance.Decisions.Count > 0)
            lines.Add($"decisions: {string.Join(" | ", d.Provenance.Decisions)}");
        return string.Join("\n", lines);
    }

    public static LearnedConclusionChunk ToChunk(LearnedConclusionDoc d, float[] vector) => new(
        d.Id, Content(d), vector, d.Kind,
        d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance,
        d.Confidence, d.CreatedAt);
}
