using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class LearnedConclusionProjectionTests
{
    private static LearnedConclusionDoc Doc(ConclusionScope? scope = null) => new()
    {
        Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"),
        Kind = KnowledgeKinds.Material,
        Scope = scope ?? new("Ba", "sulfate", "HDPE", "packaging", "EU", null),
        Finding = "Barium sulfate is unsuitable for XRF-marked HDPE where Ti is present.",
        Confidence = 0.7,
        Provenance = new(["proj-1"],
            ["revision proj-1|revision|discovery|aaaa1111 — target: Ba tier — operator reason: overlaps the Ti K-beta line"]),
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    [Fact]
    public void Content_CarriesEverythingTheReaderCanSee()
    {
        var content = LearnedConclusionProjection.Content(Doc());

        // The reader selects ONLY id + content, so each of these must be IN the string — not merely in a
        // sibling index field.
        Assert.Contains("Barium sulfate is unsuitable", content);       // the finding
        Assert.Contains("Ba", content);                                 // scope, for term overlap
        Assert.Contains("HDPE", content);
        Assert.Contains("0.70", content);                               // confidence — recency+confidence break ties
        Assert.Contains("2026-07-13T10:00:00Z", content);               // recency
        Assert.Contains("proj-1", content);                             // provenance
        Assert.Contains("overlaps the Ti K-beta line", content);        // THE OPERATOR'S VERBATIM REASON
    }

    [Fact]
    public void Content_WithAnEmptyScope_IsStillWellFormed()
    {
        var content = LearnedConclusionProjection.Content(Doc(new(null, null, null, null, null, null)));
        Assert.StartsWith("[material]\n", content);                     // no dangling separator
        Assert.Contains("Barium sulfate is unsuitable", content);
    }

    [Fact]
    public void ToChunk_MapsScopeToTheFilterableFields_AndKeepsTheVector()
    {
        var chunk = LearnedConclusionProjection.ToChunk(Doc(), new float[3072]);

        Assert.Equal(Doc().Id, chunk.Id);
        Assert.Equal(KnowledgeKinds.Material, chunk.Kind);
        Assert.Equal("Ba", chunk.Element);
        Assert.Equal("HDPE", chunk.Material);
        Assert.Equal(3072, chunk.ContentVector.Length);
        Assert.Equal(LearnedConclusionProjection.Content(Doc()), chunk.Content);
    }
}
