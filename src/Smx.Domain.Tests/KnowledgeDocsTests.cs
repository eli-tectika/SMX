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
        Assert.Equal("unreviewed", s.ReviewStatus);
        Assert.Empty(s.LinkedProjects);
    }
}
