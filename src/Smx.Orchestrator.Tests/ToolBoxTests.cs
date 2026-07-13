using Microsoft.Extensions.AI;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    private static ToolBox Box(
        Smx.Domain.IKnowledgeStore? knowledge = null,
        Smx.Domain.Tools.ILearnedConclusionsSearch? learnedConclusions = null)
    {
        var search = new FakeSearch();
        return new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            knowledge ?? new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(),
            learnedConclusions ?? new FakeLearnedConclusionsSearch());
    }

    [Fact]
    public void DiscoveryTools_ExposeCatalogCompatibilityReference()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["lookup_compatibility", "search_catalog", "search_learned_conclusions", "search_reference"], names);
    }

    [Fact]
    public void RegulatoryTools_ExposeRegulatorySdsReference_NoCompatibility()
    {
        var names = Box().RegulatoryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["search_reference", "search_regulatory", "search_sds"], names);
        Assert.DoesNotContain("lookup_compatibility", names);
    }

    [Fact]
    public async Task SearchCatalog_RendersEmptyAsNoMatchNote()
    {
        var json = await Box().SearchCatalogAsync("Xx", default);
        Assert.Contains("no matches", json);
    }

    [Fact]
    public void IntakeTools_IncludeMarkerLibraryAndLearnedConclusions()
    {
        var names = Box().IntakeTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Contains("search_marker_library", names);
        Assert.Contains("search_learned_conclusions", names);
    }

    [Fact]
    public void DiscoveryTools_IncludeLearnedConclusions()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).ToArray();
        Assert.Contains("search_learned_conclusions", names);
    }

    [Fact]
    public async Task SearchMarkerLibrary_EmptyStore_ReturnsNoMatchesSentinel()
    {
        var json = await Box().SearchMarkerLibraryAsync("anti-counterfeit", "label", "overt", default);
        Assert.Contains("no matches", json);
    }

    [Fact]
    public async Task SearchLearnedConclusions_EmptyIndex_ReturnsNoMatchesSentinel()
    {
        var json = await Box().SearchLearnedConclusionsAsync("zr bottle", default);
        Assert.Contains("no matches", json);
    }

    // The agent must never see a retrieval score. search_learned_conclusions is a HYBRID query, so its score is
    // RRF (~0.01-0.03); search_regulatory / search_sds / search_reference are BM25 (~1-10). Both land in the same
    // context window, on incomparable scales, with nothing to tell the model so — it reads 0.016 as weak evidence
    // and discounts the prior conclusion the knowledge loop exists to surface. It cites by reference and weighs the
    // calibrated `confidence:` inside a conclusion's own content, so it needs no score at all.
    [Fact]
    public async Task SearchRegulatory_RendersReferenceAndContent_ButNeverTheScore()
    {
        var search = new FakeSearch
        {
            Results = [new Smx.Domain.Tools.RetrievedChunk(
                "regulatory", "regulatory-corpus/reach-svhc-12", "Barium sulfate is not on the SVHC candidate list.", 4.2)],
        };
        var box = new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch());

        var json = await box.SearchRegulatoryAsync("is Ba an SVHC?", default);

        Assert.Contains("regulatory-corpus/reach-svhc-12", json);
        Assert.Contains("not on the SVHC candidate list", json);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4.2", json);
    }

    [Fact]
    public async Task SearchLearnedConclusions_RendersReferenceAndContent_ButNeverTheRrfScore()
    {
        var learned = new FakeLearnedConclusionsSearch();
        learned.Results.Add(new Smx.Domain.Tools.RetrievedChunk(
            "learned-conclusions", "learned-conclusions/lc-42",
            "[material] Ba · HDPE\nBarium overlaps the Ti K-beta line.\nconfidence: 0.70 · recorded: 2026-07-13T10:00:00Z",
            0.0163));

        var json = await Box(learnedConclusions: learned).SearchLearnedConclusionsAsync("zr bottle", default);

        Assert.Contains("learned-conclusions/lc-42", json);
        Assert.Contains("Ti K-beta", json);
        Assert.Contains("confidence: 0.70", json);            // the calibrated number the agent SHOULD weigh
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0163", json);                // the RRF number it cannot calibrate
    }

    private static async Task<Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore> SeededMarkerStore()
    {
        var knowledge = new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore();
        await knowledge.UpsertMarkerAsync(new Smx.Domain.Records.MarkerLibraryDoc
        {
            Id = Smx.Domain.Records.KnowledgeIds.Marker("m1"),
            Composition = new(["Zr"], 200, "1:0"), ValidatedFor = new("anti-counterfeit", "label", "overt"),
            SourceProject = "p1", CreatedAt = "t",
        });
        return knowledge;
    }

    // The regression guard. This is the exact call shape the tool description + IntakeAgent instructions
    // induce. It used to return the "no matches" sentinel — the free-text store CONTAINS-ed the combined
    // phrase against each validatedFor field independently, so a perfectly matching marker was invisible
    // and the reuse-first feature (design §6.2) was dead on arrival.
    [Fact]
    public async Task SearchMarkerLibrary_AllThreeDimensions_FindsSeededMarker()
    {
        var json = await Box(knowledge: await SeededMarkerStore())
            .SearchMarkerLibraryAsync("anti-counterfeit", "label", "overt", default);
        Assert.Contains("anti-counterfeit", json);
        Assert.DoesNotContain("no matches", json);
    }

    [Fact]
    public async Task SearchMarkerLibrary_ReturnsSeededMatch()
    {
        var json = await Box(knowledge: await SeededMarkerStore())
            .SearchMarkerLibraryAsync("anti-counterfeit", null, null, default);
        Assert.Contains("anti-counterfeit", json);
        Assert.DoesNotContain("no matches", json);
    }

    // The tests above call the C# method directly, which cannot catch a schema/binding defect: the model
    // does not call the method, it calls the AIFunction. These two drive the real AIFunction from
    // IntakeTools() with an argument dictionary, exactly as the agent runtime does.
    private static AIFunction MarkerTool(ToolBox box) =>
        (AIFunction)box.IntakeTools().Single(t => t.Name == "search_marker_library");

    [Fact]
    public async Task SearchMarkerLibraryTool_PartialArguments_BindsAndFindsMarker()
    {
        // The tool description tells the model "omit a dimension to leave it unconstrained", and an intake
        // with an application + material but no objective is ordinary. If those params are not optional in
        // the emitted schema, this call throws and reuse-first dies in exactly the path FIX 1 resurrected.
        var tool = MarkerTool(Box(knowledge: await SeededMarkerStore()));
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["application"] = "anti-counterfeit" });
        Assert.DoesNotContain("no matches", result?.ToString());
        Assert.Contains("anti-counterfeit", result?.ToString());
    }

    [Fact]
    public async Task SearchMarkerLibraryTool_AllArguments_BindsAndFindsMarker()
    {
        var tool = MarkerTool(Box(knowledge: await SeededMarkerStore()));
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["application"] = "anti-counterfeit", ["material"] = "label", ["objective"] = "overt",
        });
        Assert.DoesNotContain("no matches", result?.ToString());
        Assert.Contains("anti-counterfeit", result?.ToString());
    }

    [Fact]
    public async Task SearchMarkerLibrary_NonMatchingDimension_ReturnsNoMatchesSentinel()
    {
        // AND semantics: the application matches but the material does not — no reuse candidate.
        var json = await Box(knowledge: await SeededMarkerStore())
            .SearchMarkerLibraryAsync("anti-counterfeit", "bottle", null, default);
        Assert.Contains("no matches", json);
    }
}
