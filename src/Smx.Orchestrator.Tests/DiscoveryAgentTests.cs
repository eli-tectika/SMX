using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DiscoveryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Y", "Kα", "V", null)],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private const string Valid = """
    { "substances": [
      { "componentId": "bottle", "element": "Y", "form": "2-ethylhexanoate", "cas": "136-25-4",
        "particleSize": null, "solvent": "mineral spirits", "preferred": true, "tier": "A",
        "rationale": "clean XRF (V), catalog-available",
        "citations": [{ "source": "catalog", "reference": "ref-catalog/product|Y|x", "retrievedAt": "t" }] } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesCandidatesDoc()
    {
        var result = await DiscoveryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), null, default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|candidates", result.Output!.Id);
        Assert.Single(result.Output.Substances);
        Assert.Equal("A", result.Output.Substances[0].Tier);
    }

    [Fact]
    public async Task Candidate_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"componentId\": \"bottle\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task Candidate_WithElementNotInPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Cd\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("not in the element pool", result.Error);
    }

    [Fact]
    public async Task Candidate_WithoutCitation_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"catalog\", \"reference\": \"ref-catalog/product|Y|x\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }

    private static CandidateSubstance Candidate(string tier, bool preferred, string cas, params Citation[] citations) =>
        new("bottle", "Y", "2-ethylhexanoate", cas, null, null, preferred, tier, "because", citations);

    // "web:<host>" is exactly what ToolBox.SearchWebAsync stamps on a hit, which is what makes a web-derived
    // citation machine-identifiable this far downstream.
    private static readonly Citation WebCite = new("web:pubchem.ncbi.nlm.nih.gov", "https://pubchem.ncbi.nlm.nih.gov/compound/1", "2026-07-13T10:00:00Z");
    private static readonly Citation CatalogCite = new("catalog", "ref-catalog/product|Y|y-2eh", "2026-07-13T10:00:00Z");

    // RAIL 1. The web can SUGGEST a marker; only the catalog and the reference corpus can ENDORSE one.
    // Tier A is an endorsement.
    [Fact]
    public void WebOnlyCitations_CannotBeTierA()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", false, "80326-98-3", WebCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("Tier A", error);
    }

    [Fact]
    public void WebOnlyCitations_CannotBePreferred()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", true, "80326-98-3", WebCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("preferred", error);
    }

    [Fact]
    public void WebOnlyCitations_AreFineAtTierB()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", false, "80326-98-3", WebCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // A web hit that is CORROBORATED by the catalog is no longer a web-only claim — that is exactly the
    // behaviour the tool description asks for, so it must be allowed at Tier A.
    [Fact]
    public void WebCitationCorroboratedByTheCatalog_MayBeTierA()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", WebCite, CatalogCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // RAIL 2. A CAS with a bad check digit is provably wrong, and a wrong CAS silently clears the WRONG
    // substance through the regulatory gate.
    [Fact]
    public void InvalidCasCheckDigit_IsRejected()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", false, "80326-98-4", CatalogCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("check digit", error);
    }

    [Fact]
    public void ValidCas_Passes()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", CatalogCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }
}
