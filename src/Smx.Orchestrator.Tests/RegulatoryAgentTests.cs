using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class RegulatoryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "element gate",
            new Citation("regulatory", "regulatory-index/reach-17", "t"))],
    };

    private static CandidateSubstance Candidate() =>
        new("bottle", "Cd", "sulfide", "1306-23-6", null, null, true, "A", "provided", []);

    private const string Valid = """
    { "dimensions": [
      { "dimension": "ElementGate", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-e23", "retrievedAt": "t" }],
        "confidence": 0.98, "rationale": "Cd restricted by REACH Annex XVII entry 23" },
      { "dimension": "ApplicationCheck", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr-hm", "retrievedAt": "t" }],
        "confidence": 0.95, "rationale": "PPWR heavy-metal cap" },
      { "dimension": "Hazard", "status": "Fail",
        "citations": [{ "source": "sds", "reference": "sds-index/cd-ghs", "retrievedAt": "t" }],
        "confidence": 0.97, "rationale": "carcinogenic H350" } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesVerdictDoc_ThreeDimensions()
    {
        var result = await RegulatoryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), Candidate(), null, default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|verdict|1306-23-6|bottle", result.Output!.Id);
        Assert.Equal(VerdictStatus.Fail, result.Output.Overall);
        Assert.Equal(3, result.Output.Dimensions.Count);
    }

    [Fact]
    public async Task IncludingCompatibilityDimension_IsRejected()
    {
        var bad = Valid.Replace("\"dimension\": \"Hazard\"", "\"dimension\": \"Compatibility\"");
        var agent = new ScriptedAgent(bad, Valid);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        Assert.True(result.Succeeded);
        Assert.Contains("exactly the three dimensions", agent.Received[1]);
    }

    [Fact]
    public async Task UncitedDimension_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"sds\", \"reference\": \"sds-index/cd-ghs\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }

    [Fact]
    public async Task PromptCarriesCandidate_ScopeAndRestrictedList()
    {
        var agent = new ScriptedAgent(Valid);
        await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);
        var prompt = agent.Received[0];
        Assert.Contains("1306-23-6", prompt);
        Assert.Contains("reach-annex-xvii", prompt);
        Assert.Contains("Pb", prompt);
    }

    // ---- the agent's PROPOSAL (Plan 4) -------------------------------------------------------------
    // Validate demands all three dimensions, so a proposal fixture needs all three. These are clean and
    // cited: the only shape on which proposing "recommended" is legitimate.
    private const string PassDimensions = """
      { "dimension": "ElementGate", "status": "Pass",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" }],
        "confidence": 0.9, "rationale": "not listed" },
      { "dimension": "ApplicationCheck", "status": "Pass",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr", "retrievedAt": "t" }],
        "confidence": 0.9, "rationale": "no restriction binds this application" },
      { "dimension": "Hazard", "status": "Pass",
        "citations": [{ "source": "sds", "reference": "sds-index/ghs", "retrievedAt": "t" }],
        "confidence": 0.9, "rationale": "no CMR classification" }
    """;

    private static List<DimensionVerdict> Dims(VerdictStatus status = VerdictStatus.Pass) =>
    [
        new("ElementGate", status, [new Citation("regulatory", "reach-17", "t")], 0.9, "r"),
        new("ApplicationCheck", VerdictStatus.Pass, [new Citation("regulatory", "ppwr", "t")], 0.9, "r"),
        new("Hazard", VerdictStatus.Pass, [new Citation("sds", "ghs", "t")], 0.9, "r"),
    ];

    [Fact]
    public async Task Regulatory_ProposesADetermination_WithAReason()
    {
        // The proposal is what turns "the operator must determine EVERY cell" from an authoring burden into
        // a confirmation. The agent does the reading; the human does the deciding.
        var agent = new ScriptedAgent($$"""
            { "dimensions": [{{PassDimensions}}],
              "proposedDetermination": "recommended", "proposedReason": "clean on all three dimensions" }
            """);

        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Determinations.Recommended, result.Output!.ProposedDetermination);
        Assert.Equal("clean on all three dimensions", result.Output.ProposedReason);
        Assert.Null(result.Output.Determination);            // THE OPERATOR HAS NOT SPOKEN.
        Assert.Null(result.Output.DeterminationReason);
        Assert.False(result.Output.EvidenceReviewed);
    }

    [Fact]
    public async Task Regulatory_CannotWriteTheOperatorsDeterminationField_EvenWhenTheModelTries()
    {
        // A model emitting `"determination":"recommended"` must NOT have it land on the operator's field.
        // That field is a SIGNATURE. If this test fails, the agent can sign the regulatory gate — which is
        // the single thing Law 9 exists to make impossible. It is a design alarm, not a test to adjust.
        // The model here ALSO emits a legitimate proposal, and we assert the run SUCCEEDED and the proposal
        // landed: without that, the nulls below would also be satisfied by a run that failed outright, and
        // by a mapping bug that drops the proposal onto Determination.
        var agent = new ScriptedAgent($$"""
            { "dimensions": [{{PassDimensions}}],
              "proposedDetermination": "recommended", "proposedReason": "clean on all three dimensions",
              "determination": "recommended", "determinationReason": "I hereby approve this" }
            """);

        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);

        // The DTO has no such members, so STJ discards them: the guard is structural, not a check.
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Determinations.Recommended, result.Output!.ProposedDetermination);
        Assert.Null(result.Output.Determination);
        Assert.Null(result.Output.DeterminationReason);
    }

    [Fact]
    public void Validate_RejectsAProposalWithNoReason()
    {
        // Every determination — recommend OR reject — carries a reason. A bare "recommended" is precisely
        // the rubber stamp the whole design is against.
        var output = new RegulatoryOutput
        {
            Dimensions = Dims(),
            ProposedDetermination = Determinations.Recommended,
            ProposedReason = "   ",
        };
        Assert.Contains("reason", RegulatoryAgent.Validate(output)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAProposalThatIsNeitherRecommendedNorRejected()
    {
        var output = new RegulatoryOutput
        {
            Dimensions = Dims(),
            ProposedDetermination = "probably fine",
            ProposedReason = "looks ok",
        };
        Assert.NotNull(RegulatoryAgent.Validate(output));
    }

    [Fact]
    public void Validate_AcceptsAVerdictWithNoProposalAtAll()
    {
        // A proposal is OPTIONAL on purpose. Demanding one would burn three model retries and sink an
        // otherwise perfectly good verdict into needs-review whenever the model is merely coy. No pre-fill
        // just means the operator authors that cell by hand, exactly as before — the safe direction.
        Assert.Null(RegulatoryAgent.Validate(new RegulatoryOutput { Dimensions = Dims() }));
    }

    [Fact]
    public void Validate_RefusesToPropose_Recommended_OnAFailingVerdict()
    {
        // The R.E. may override a Fail — that is what a human gate is for. But an agent that pre-fills
        // "recommended" on a red cell is training the operator to rubber-stamp, which is the exact
        // behaviour this design exists to prevent. An override of a Fail is the human's to author, alone.
        var output = new RegulatoryOutput
        {
            Dimensions = Dims(VerdictStatus.Fail),
            ProposedDetermination = Determinations.Recommended,
            ProposedReason = "the listing is probably superseded",
        };
        Assert.NotNull(RegulatoryAgent.Validate(output));
    }

    [Fact]
    public void Validate_RefusesToPropose_Recommended_WhenADimensionIsNeedsReview()
    {
        // NeedsReview means the agent's own tools returned nothing decisive. Recommending on that is
        // "assume clean" — the one thing the standing Instructions forbid.
        var output = new RegulatoryOutput
        {
            Dimensions = Dims(VerdictStatus.NeedsReview),
            ProposedDetermination = Determinations.Recommended,
            ProposedReason = "nothing turned up, looks fine",
        };
        Assert.NotNull(RegulatoryAgent.Validate(output));
    }

    [Fact]
    public void Validate_Accepts_Rejected_OnAFailingVerdict()
    {
        // The rule above is one-directional: proposing to REJECT a failing substance is the agent doing
        // exactly its job, and must not be caught by it.
        var output = new RegulatoryOutput
        {
            Dimensions = Dims(VerdictStatus.Fail),
            ProposedDetermination = Determinations.Rejected,
            ProposedReason = "Cd is restricted by REACH Annex XVII entry 23",
        };
        Assert.Null(RegulatoryAgent.Validate(output));
    }
}
