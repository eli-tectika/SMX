using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class LearnedConclusionProjectionTests
{
    // The fixture is chosen so the assertions cannot pass by accident:
    //   - the Finding must NOT contain the element symbol ("Ba") as a substring, or an assertion that the
    //     scope line carries the element would pass on the "Ba" inside "Barium";
    //   - SourceProjects must NOT be the project id embedded in the Decisions string, or an assertion that
    //     the provenance line carries the source projects would pass on the id inside the decision text.
    private const string RevisionId = "proj-1|revision|discovery|aaaa1111";

    private static LearnedConclusionDoc Doc(ConclusionScope? scope = null, ConclusionProvenance? provenance = null) => new()
    {
        Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, RevisionId),
        Kind = KnowledgeKinds.Material,
        Scope = scope ?? new("Ba", "sulfate", "HDPE", "packaging", "EU", null),
        Finding = "This form is unsuitable for XRF-marked HDPE where titanium is present.",
        Confidence = 0.7,
        Provenance = provenance ?? new(["proj-source-9"],
            [$"revision {RevisionId} — target: Ba tier — operator reason: overlaps the Ti K-beta line"]),
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    /// A distinctive vector: an all-zero array cannot tell "the caller's vector came through" apart from
    /// "ToChunk allocated its own empty one" — and a zero vector silently kills the vector half of hybrid search.
    private static float[] Vector()
    {
        var v = new float[3072];
        v[0] = 0.11f;
        v[1] = -0.22f;
        v[3071] = 0.99f;
        return v;
    }

    [Fact]
    public void Content_CarriesEverythingTheReaderCanSee()
    {
        var content = LearnedConclusionProjection.Content(Doc());

        // The reader selects ONLY id + content, so each of these must be IN the string — not merely in a
        // sibling index field. Assert the COMPOSED lines: a bare Contains("Ba") would pass on any incidental
        // occurrence elsewhere in the text and would not notice the scope line losing the element entirely.
        Assert.StartsWith("[material] Ba · sulfate · HDPE · packaging · EU\n", content);   // scope, for term overlap
        Assert.Contains("This form is unsuitable", content);                               // the finding
        Assert.Contains("confidence: 0.70 · recorded: 2026-07-13T10:00:00Z", content);     // confidence + recency break ties
        Assert.Contains("source projects: proj-source-9", content);                        // provenance
        Assert.Contains("overlaps the Ti K-beta line", content);                           // THE OPERATOR'S VERBATIM REASON
    }

    [Fact]
    public void Content_WithAnEmptyScope_IsStillWellFormed()
    {
        var content = LearnedConclusionProjection.Content(Doc(new(null, null, null, null, null, null)));
        Assert.StartsWith("[material]\n", content);                     // no dangling separator
        Assert.Contains("This form is unsuitable", content);
    }

    [Fact]
    public void Content_WithNoProvenance_EmitsNoDanglingLabels()
    {
        var content = LearnedConclusionProjection.Content(Doc(provenance: new([], [])));
        Assert.DoesNotContain("source projects:", content);
        Assert.DoesNotContain("decisions:", content);
        Assert.EndsWith("2026-07-13T10:00:00Z", content);               // no trailing empty line
    }

    [Fact]
    public void ToChunk_MapsEveryScopeField_AndKeepsTheCallersVector()
    {
        var chunk = LearnedConclusionProjection.ToChunk(Doc(), Vector());

        Assert.Equal(LearnedConclusionProjection.SearchKey(Doc().Id), chunk.Id);
        Assert.Equal(LearnedConclusionProjection.Content(Doc()), chunk.Content);
        Assert.Equal(KnowledgeKinds.Material, chunk.Kind);

        // ALL SIX scope fields — Application and Market are adjacent `string?` positional args, so asserting
        // only a couple of them lets a silent swap compile and pass.
        Assert.Equal("Ba", chunk.Element);
        Assert.Equal("sulfate", chunk.Form);
        Assert.Equal("HDPE", chunk.Material);
        Assert.Equal("packaging", chunk.Application);
        Assert.Equal("EU", chunk.Market);
        Assert.Null(chunk.Substance);

        Assert.Equal(0.7, chunk.Confidence);
        Assert.Equal("2026-07-13T10:00:00Z", chunk.CreatedAt);

        // The caller's actual values, not merely an array of the right length.
        Assert.Equal(3072, chunk.ContentVector.Length);
        Assert.Equal(0.11f, chunk.ContentVector[0]);
        Assert.Equal(-0.22f, chunk.ContentVector[1]);
        Assert.Equal(0.99f, chunk.ContentVector[3071]);
    }

    [Fact]
    public void SearchKey_IsLegalForAzureSearch_Deterministic_AndCollisionFree()
    {
        // The real, pipe-delimited Cosmos id — the exact input that would be rejected if pushed raw.
        var cosmosId = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, RevisionId);
        Assert.Contains('|', cosmosId);                                 // guard: the input really is unsafe

        var key = LearnedConclusionProjection.SearchKey(cosmosId);
        Assert.Matches("^[a-zA-Z0-9_\\-=]+$", key);                     // letters, digits, '_', '-', '=' ONLY

        // Redelivery of the same revision must upsert one document, not accumulate duplicates — the whole
        // point of KnowledgeIds.RevisionConclusion being decision-keyed, and it must survive this mapping.
        Assert.Equal(key, LearnedConclusionProjection.SearchKey(cosmosId));

        var other = LearnedConclusionProjection.SearchKey(
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|bbbb2222"));
        Assert.NotEqual(key, other);
    }

    [Fact]
    public void Chunk_PinsTheIndexFieldNames_WithoutRelyingOnASerializerPolicy()
    {
        // Serialized with NO naming policy: the wire names must come from the type itself, because nothing
        // in Smx.Backend.sln registers a camelCase serializer on a SearchClient.
        var json = JsonSerializer.Serialize(LearnedConclusionProjection.ToChunk(Doc(), Vector()), new JsonSerializerOptions());

        foreach (var field in new[] { "\"id\":", "\"content\":", "\"contentVector\":", "\"kind\":", "\"element\":",
                                      "\"form\":", "\"material\":", "\"application\":", "\"market\":",
                                      "\"substance\":", "\"confidence\":", "\"createdAt\":" })
            Assert.Contains(field, json);

        Assert.DoesNotContain("\"Content\":", json);                    // no PascalCase leakage
        Assert.DoesNotContain("\"ContentVector\":", json);
    }
}
