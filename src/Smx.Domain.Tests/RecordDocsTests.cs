using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RecordDocsTests
{
    [Fact]
    public void VerdictId_IsDeterministic_AndPipeDelimited()
    {
        Assert.Equal("p1|verdict|39049-04-2|bottle", RecordIds.Verdict("p1", "39049-04-2", "bottle"));
        Assert.Equal("p1|constraints", RecordIds.Constraints("p1"));
        Assert.Equal("p1|matrix", RecordIds.Matrix("p1"));
    }

    [Fact]
    public void ProjectDoc_SerializesCamelCase_WithTypeDiscriminator()
    {
        var doc = ProjectDoc.Create("p1", "Acme", "Shampoo bottle", JsonDocument.Parse("{}").RootElement);
        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"type\":\"project\"", json);
        Assert.Contains("\"projectId\":\"p1\"", json);
        Assert.Contains("\"intake\"", json); // stages seeded
        var back = JsonSerializer.Deserialize<ProjectDoc>(json, Json.Options)!;
        Assert.Equal("pending", back.Stages["intake"].Status);
    }

    [Theory]
    [InlineData(new[] { "Pass", "Pass" }, "Pass")]
    [InlineData(new[] { "Pass", "Conditional" }, "Conditional")]
    [InlineData(new[] { "Conditional", "NeedsReview" }, "NeedsReview")]
    [InlineData(new[] { "NeedsReview", "Fail" }, "Fail")]
    public void Verdict_Overall_IsWorstOfDimensions(string[] statuses, string expected)
    {
        var dims = statuses.Select((s, i) => new DimensionVerdict(
            Dimension: ((VerdictDimension)i).ToString(),
            Status: Enum.Parse<VerdictStatus>(s),
            Citations: [new Citation("reg-index", "doc-1#chunk-3", "2026-07-08T00:00:00Z")],
            Confidence: 0.9,
            Rationale: "r")).ToList();
        Assert.Equal(Enum.Parse<VerdictStatus>(expected), VerdictDoc.Fold(dims));
    }

    [Fact]
    public void Verdict_RoundTrips()
    {
        var v = new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new DimensionVerdict("ElementGate", VerdictStatus.Pass,
                [new Citation("reg-index", "reach-annex17#e23", "2026-07-08T00:00:00Z")], 0.95, "not listed")],
        };
        var back = JsonSerializer.Deserialize<VerdictDoc>(JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.Equal(VerdictStatus.Pass, back.Overall);
        Assert.Single(back.Dimensions[0].Citations);
    }
}
