using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DecisionAgentTests
{
    // ---- fixtures -------------------------------------------------------------------------------------
    // Components ["bottle"], assembled rows for cas-zr/cas-y (DecisionAssembler's shapes), and a DosingDoc
    // with ONE finalized code: Zr@100 + Y@44, whose DERIVED signature is "Zr:Y = 1.00:0.44".

    private static DecisionRow Row(string cas, string element, double ppm) =>
        new(cas, element, Determinations.Recommended, ppm,
            new ClearedCriteria(Regulatory: true, Dosing: true, Cost: true),
            new TraceRefs($"p1|verdict|{cas}|bottle", "p1|dosing", "p1|cost"));

    private static IReadOnlyList<ComponentDecision> Assembled() =>
        [new("bottle", Rows: [Row("cas-y", "Y", 44), Row("cas-zr", "Zr", 100)], ProposedCode: null)];

    private static DosingDoc Dosing() => new()
    {
        Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "t",
        Codes = [new MarkerCode("bottle",
            [new CodeMarker("cas-zr", "Zr", 100, 0.74, 1, 2), new CodeMarker("cas-y", "Y", 44, 0.7, 1, 2)], "r")],
    };

    private static DecisionPickOutput Pick(
        string comp = "bottle", string ratio = "Zr:Y = 1.00:0.44",
        List<string>? cas = null, string rationale = "covers both") =>
        new(comp, ratio, cas ?? ["cas-zr", "cas-y"], rationale);

    private static DecisionOutput Output(params DecisionPickOutput[] picks) => new() { Picks = [.. picks] };

    // What a ScriptedAgent replies with. It ALSO tries to smuggle a confirmation in every place a model
    // could put one — a pick-level and a top-level "confirmedCode" — which the output contract has no field
    // for, so deserialization drops them and the doc built in code cannot carry them.
    private const string Valid = """
    { "picks": [ { "componentId": "bottle", "ratioSignature": "Zr:Y = 1.00:0.44",
        "markerCas": ["cas-zr", "cas-y"], "rationale": "covers both criteria at lowest cost",
        "confirmedCode": "Zr:Y = 1.00:0.44", "confirmedBy": "model", "confirmedReason": "smuggled" } ],
      "confirmedCode": "Zr:Y = 1.00:0.44" }
    """;

    // ---- RunAsync: the pick is a PROPOSAL, and the code builds the doc ------------------------------

    [Fact]
    public async Task RunAsync_AValidPick_BecomesAProposalNeverAConfirmation()
    {
        var result = await DecisionAgent.RunAsync(new ScriptedAgent(Valid), Assembled(), Dosing(), null, default);

        Assert.True(result.Succeeded, result.Error);
        var bottle = Assert.Single(result.Output!.Components);
        Assert.NotNull(bottle.ProposedCode);
        Assert.Equal("Zr:Y = 1.00:0.44", bottle.ProposedCode!.RatioSignature);
        Assert.Equal("covers both criteria at lowest cost", bottle.ProposedCode.Rationale);
        // The agent CANNOT confirm — different fields, never written here, even though the model's JSON
        // carried "confirmedCode" at both levels. The VP's fields belong to the endpoint alone (Law 9).
        Assert.Null(bottle.ConfirmedCode);
        Assert.Null(bottle.ConfirmedBy);
        Assert.Null(bottle.ConfirmedReason);
    }

    [Fact]
    public async Task RunAsync_BuildsTheDocInCode_FromTheMatchedCodeAndTheAssembledRows()
    {
        var result = await DecisionAgent.RunAsync(new ScriptedAgent(Valid), Assembled(), Dosing(), null, default);

        Assert.True(result.Succeeded, result.Error);
        var doc = result.Output!;
        // Identity comes from the record (dosing.ProjectId), not from anything the model said.
        Assert.Equal(RecordIds.Decision("p1"), doc.Id);
        Assert.Equal("p1", doc.ProjectId);
        var bottle = Assert.Single(doc.Components);
        // The assembled rows ride through UNTOUCHED — the agent layers a proposal on top, nothing else.
        Assert.Equal(["cas-y", "cas-zr"], bottle.Rows.Select(r => r.Cas));
        // The stored MarkerCas is the MATCHED code's, and procurement stays unreleased by default.
        Assert.Equal(["cas-zr", "cas-y"], bottle.ProposedCode!.MarkerCas);
        Assert.Equal(ProcurementStatus.Unreleased, doc.Procurement.Status);
    }

    // ---- a well-formed pick validates (marker order does not matter — CAS equality is SET equality) --

    [Fact]
    public void WellFormedPick_ReturnsNull_EvenWithMarkersListedInAnotherOrder()
    {
        Assert.Null(DecisionAgent.Validate(
            Output(Pick(cas: ["cas-y", "cas-zr"])), Assembled(), Dosing()));
    }

    // ---- invariant 1: every component gets exactly one pick ------------------------------------------

    [Fact]
    public void AComponentWithNoPick_IsRejected()
    {
        var error = DecisionAgent.Validate(Output(), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("bottle", error);
        Assert.Contains("no pick", error);
    }

    [Fact]
    public void AComponentWithTwoPicks_IsRejected()
    {
        var error = DecisionAgent.Validate(Output(Pick(), Pick()), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("bottle", error);
        Assert.Contains("2 picks", error);
    }

    [Fact]
    public void APickForAComponentNotOnTheMatrix_IsRejected()
    {
        // "exactly one pick per component" cuts both ways: a pick for a component the matrix does not
        // carry is a recommendation over rows the VP will never see.
        var error = DecisionAgent.Validate(Output(Pick(), Pick(comp: "lid")), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("lid", error);
        // THIS guard's message, specifically — invariant 2 would also name 'lid' ("matches no finalized
        // code"), so a dropped unknown-component check could otherwise hide behind it.
        Assert.Contains("not on the decision matrix", error);
    }

    // ---- invariant 2: the picked code must BE one of DosingDoc.Codes ---------------------------------

    [Fact]
    public void APickWithAnInventedRatio_IsRejected()
    {
        // No finalized code renders "Zr:Y = 1.00:0.99" — the model may choose among codes, never mint one.
        var error = DecisionAgent.Validate(Output(Pick(ratio: "Zr:Y = 1.00:0.99")), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("matches no finalized code", error);
    }

    [Fact]
    public void APickGraftingAMarkerIntoARealRatio_IsRejected()
    {
        // The ratio is the real code's, but the marker set is not: cas-x rides where cas-y belongs. The
        // match is signature AND exact CAS set, so a grafted marker cannot hide behind a genuine ratio.
        var error = DecisionAgent.Validate(Output(Pick(cas: ["cas-zr", "cas-x"])), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("matches no finalized code", error);
        Assert.Contains("cas-x", error);
    }

    // ---- invariant 3: rationale non-blank -------------------------------------------------------------

    [Fact]
    public void ABlankRationale_IsRejected()
    {
        var error = DecisionAgent.Validate(Output(Pick(rationale: "  ")), Assembled(), Dosing());
        Assert.NotNull(error);
        Assert.Contains("rationale", error);
    }

    // ---- invariant 4: a pick may not name a CAS that has no decision row ------------------------------

    [Fact]
    public void APickNamingACasWithNoDecisionRow_IsRejected()
    {
        // The code is REAL (invariant 2 passes: right component, right signature, exact CAS set) — but the
        // matrix only carries a cas-zr row, so cas-y arrives with no decision row behind it. A code minted
        // while cas-y was recommended must not carry it into the decision after the R.E.'s ruling changed.
        var assembled = new List<ComponentDecision>
        {
            new("bottle", Rows: [Row("cas-zr", "Zr", 100)], ProposedCode: null),
        };
        var error = DecisionAgent.Validate(Output(Pick()), assembled, Dosing());
        Assert.NotNull(error);
        Assert.Contains("cas-y", error);
        Assert.Contains("no decision row", error);
    }
}
