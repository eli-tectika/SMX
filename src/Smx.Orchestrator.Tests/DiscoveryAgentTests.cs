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

    // ---------------------------------------------------------------------------------------------------
    // RAIL 1, the HOSTED path. The built-in web tool composes its own query server-side, so nothing in our
    // code pre-stamps "web:" on the citations the model writes. StampWebCitations restores the stamp from a
    // fact the model cannot forge — the set of URLs the tool ACTUALLY returned — so Validate's web-only rail
    // fires identically to the proxy path. These drive StampWebCitations + Validate directly.
    // ---------------------------------------------------------------------------------------------------

    // How the model writes a web citation on the hosted path: the source is NOT pre-stamped "web:" (nothing
    // stamped it); only the reference carries the page URL. It counts as web-derived iff that URL is one the
    // hosted tool returned.
    private const string ReturnedUrl = "https://pubchem.ncbi.nlm.nih.gov/compound/1";
    private static readonly Citation HostedWebCite = new("web page", ReturnedUrl, "2026-07-13T10:00:00Z");

    [Fact]
    public void StampWebCitations_RewritesACitationPointingAtAReturnedUrl_ToWebSource()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", false, "80326-98-3", HostedWebCite)] };
        DiscoveryAgent.StampWebCitations(output, [ReturnedUrl]);
        Assert.StartsWith("web:", output.Substances[0].Citations[0].Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pubchem.ncbi.nlm.nih.gov", output.Substances[0].Citations[0].Source);
    }

    // The rail cannot be dodged by mislabelling the source: after the deterministic re-stamp a candidate whose
    // only real citation is a returned URL is web-only, so Tier A is refused — on a fact the model did not write.
    [Fact]
    public void HostedWebOnlyCandidate_IsCappedAtTierB_EvenWhenTheSourceWasMislabelled()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", false, "80326-98-3", HostedWebCite)] };
        DiscoveryAgent.StampWebCitations(output, [ReturnedUrl]);
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("Tier A", error);
    }

    // A bare host in the reference (no scheme) still matches — the model need not echo the URL verbatim.
    [Fact]
    public void StampWebCitations_MatchesABareHostReference()
    {
        var output = new DiscoveryOutput
        {
            Substances = [Candidate("A", false, "80326-98-3", new Citation("note", "pubchem.ncbi.nlm.nih.gov/compound/1", "t"))],
        };
        DiscoveryAgent.StampWebCitations(output, [ReturnedUrl]);
        Assert.Contains("Tier A", DiscoveryAgent.Validate(output, Constraints()));
    }

    // No URLs returned this turn (the proxy path, or a run that never searched) ⇒ nothing is re-stamped; a
    // legitimately catalog-sourced candidate is untouched and may be Tier A.
    [Fact]
    public void StampWebCitations_WithNoReturnedUrls_IsANoOp()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", CatalogCite)] };
        DiscoveryAgent.StampWebCitations(output, []);
        Assert.Equal("catalog", output.Substances[0].Citations[0].Source);
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // No collateral damage: a returned URL this candidate does not cite leaves its catalog citation alone.
    [Fact]
    public void StampWebCitations_LeavesACitationThatPointsElsewhere_Untouched()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", CatalogCite)] };
        DiscoveryAgent.StampWebCitations(output, [ReturnedUrl]);
        Assert.Equal("catalog", output.Substances[0].Citations[0].Source);
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // Corroboration holds on the hosted path too: a returned-URL citation ALONGSIDE a catalog one is not
    // web-only, so Tier A stands.
    [Fact]
    public void HostedWebCitationCorroboratedByCatalog_MayBeTierA()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", HostedWebCite, CatalogCite)] };
        DiscoveryAgent.StampWebCitations(output, [ReturnedUrl]);
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }
}
