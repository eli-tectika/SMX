using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class KnowledgeDocsTests
{
    [Fact]
    public void LearnedConclusion_HasDeterministicId_KindPk_AndRoundTrips()
    {
        var c = new LearnedConclusionDoc
        {
            Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.RegulatoryJudgment, "ba|label|eu"),
            Kind = KnowledgeKinds.RegulatoryJudgment,
            Scope = new ConclusionScope(Element: "Ba", Form: null, Material: "label", Application: null, Market: "EU", Substance: null),
            Finding = "Ba tier-B for labels in EU: overlaps Ti Kα.",
            Confidence = 0.8,
            Provenance = new ConclusionProvenance(["p1"], ["p1|discovery|revise"]),
            CreatedAt = "2026-07-12T00:00:00Z",
        };
        Assert.Equal("regulatory-judgment|ba|label|eu", c.Id);
        Assert.Equal(KnowledgeTypes.LearnedConclusion, c.Type);
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, c.Kind);
        var back = JsonSerializer.Deserialize<LearnedConclusionDoc>(JsonSerializer.Serialize(c, Json.Options), Json.Options)!;
        Assert.Equal("Ba", back.Scope.Element);
        Assert.Equal(0.8, back.Confidence);
        Assert.Equal(["p1"], back.Provenance.SourceProjects);
    }

    [Fact]
    public void MarkerLibrary_HasDeterministicId_AndDefaults()
    {
        var m = new MarkerLibraryDoc
        {
            Id = KnowledgeIds.Marker("acme-anti-counterfeit-label"),
            Composition = new MarkerComposition(["Zr", "Y"], 250, "2:1"),
            ValidatedFor = new ValidatedFor(Application: "anti-counterfeit", Material: "label", Objective: "overt"),
            SourceProject = "p1",
            Status = "approved",
            CreatedAt = "2026-07-12T00:00:00Z",
        };
        Assert.Equal("marker|acme-anti-counterfeit-label", m.Id);
        Assert.Equal(KnowledgeTypes.MarkerLibrary, m.Type);
        Assert.Equal(0, m.ReuseCount);

        // Only Cosmos deserializes these in production; `required` members + a positional record with an
        // IReadOnlyList are exactly the shape STJ can silently fail to rehydrate.
        var back = JsonSerializer.Deserialize<MarkerLibraryDoc>(JsonSerializer.Serialize(m, Json.Options), Json.Options)!;
        Assert.Equal(m.Id, back.Id);
        Assert.Equal(["Zr", "Y"], back.Composition.Markers);
        Assert.Equal(250, back.Composition.Ppm);
        Assert.Equal("2:1", back.Composition.Ratio);
        Assert.Equal("anti-counterfeit", back.ValidatedFor.Application);
        Assert.Equal("label", back.ValidatedFor.Material);
        Assert.Equal("overt", back.ValidatedFor.Objective);
        Assert.Equal(MarkerStatus.Approved, back.Status);
        Assert.Equal("p1", back.SourceProject);
    }

    [Fact]
    public void MsdsRegistry_KeyedByCas_WithDefaults()
    {
        var s = new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7",
            Supplier = "Acme", Version = "3", Date = "2025-01-01",
        };
        Assert.Equal("msds|13463-67-7", s.Id);
        Assert.Equal(KnowledgeTypes.MsdsRegistry, s.Type);
        Assert.Equal("unreviewed", s.ReviewStatus);        // the constant must not drift from the wire value
        Assert.Equal(MsdsReviewStatus.Unreviewed, s.ReviewStatus);
        Assert.Null(s.ReviewedAt);
        Assert.Empty(s.LinkedProjects);

        s.ReviewStatus = MsdsReviewStatus.Reviewed;
        s.ReviewedAt = "2026-07-12T09:30:00.0000000+00:00";
        s.LinkedProjects.Add("p1");
        var back = JsonSerializer.Deserialize<MsdsRegistryDoc>(JsonSerializer.Serialize(s, Json.Options), Json.Options)!;
        Assert.Equal("13463-67-7", back.Cas);
        Assert.Equal("Acme", back.Supplier);
        Assert.Equal("3", back.Version);
        Assert.Equal("2025-01-01", back.Date);
        Assert.Equal(MsdsReviewStatus.Reviewed, back.ReviewStatus);
        Assert.Equal("2026-07-12T09:30:00.0000000+00:00", back.ReviewedAt);   // the gate's signature must survive Cosmos
        Assert.Equal(["p1"], back.LinkedProjects);
    }
}
