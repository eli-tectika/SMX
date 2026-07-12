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

    [Fact]
    public void CandidatesDoc_HasDeterministicId_AndCandidatesType()
    {
        var doc = new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new("bottle", "Y", "2-ethylhexanoate", "136-25-4", "sub-micron", "mineral spirits", true, "A", "clean XRF, catalog-available", [new Citation("catalog", "ref-catalog/product|Y|x", "t")])],
        };
        Assert.Equal("p1|candidates", doc.Id);
        Assert.Equal(RecordTypes.Candidates, doc.Type);
        Assert.Equal("A", doc.Substances[0].Tier);
        Assert.True(doc.Substances[0].Preferred);
    }

    [Fact]
    public void ElementPool_CarriesComponentAndSignalNote()
    {
        var pool = new ElementPool("liquid", "Sc", "Kα", "L", "small-amount peak");
        Assert.Equal("liquid", pool.Component);
        Assert.Equal("L", pool.Status);
        Assert.Equal("small-amount peak", pool.SignalNote);
    }

    [Fact]
    public void ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix()
    {
        var p = ProjectDoc.Create("p1", "Acme", "P", System.Text.Json.JsonDocument.Parse("{}").RootElement);
        Assert.True(p.Stages.ContainsKey(Stages.Intake));
        Assert.True(p.Stages.ContainsKey(Stages.Discovery));
        Assert.True(p.Stages.ContainsKey(Stages.Regulatory));
        Assert.True(p.Stages.ContainsKey(Stages.Matrix));
        Assert.False(p.Stages.ContainsKey("screening"));
        Assert.Equal(4, p.Stages.Count);
    }

    [Fact]
    public void ConstraintsDoc_CarriesElementPools_AndProvidedCandidates()
    {
        var c = new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Y", "Kα", "V", null)],
            ProvidedCandidates = [new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "provided", [])],
            ClientRestrictedList = ["Pb"],
            DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
        };
        Assert.Single(c.ElementPools);
        Assert.Single(c.ProvidedCandidates);
        Assert.Equal("V", c.ElementPools[0].Status);
    }

    [Fact]
    public void VerdictDoc_CarriesOperatorReviewFields_DefaultingUnset()
    {
        var v = new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodec",
        };
        Assert.False(v.EvidenceReviewed);
        Assert.Null(v.Determination);
        Assert.Null(v.DeterminationReason);

        v.EvidenceReviewed = true;
        v.Determination = "rejected";
        v.DeterminationReason = "EU Cosmetics Annex III";
        var back = System.Text.Json.JsonSerializer.Deserialize<VerdictDoc>(
            System.Text.Json.JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.True(back.EvidenceReviewed);
        Assert.Equal("rejected", back.Determination);
        Assert.Equal("EU Cosmetics Annex III", back.DeterminationReason);
    }
}
