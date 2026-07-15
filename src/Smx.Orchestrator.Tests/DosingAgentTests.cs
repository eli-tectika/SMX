using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DosingAgentTests
{
    // ---- fixtures -------------------------------------------------------------------------------------

    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand", BatchMassKg: 250.0)],
    };

    // The MEASURED detection floors code hands the agent. Zr detects at 8.5 ppm, Y at 6.0 — the recommended
    // ppm must sit strictly above these, and code (not the model) supplies them.
    private static Dictionary<(string ComponentId, string Element), Floor> Floors() => new()
    {
        [("bottle", "Zr")] = new Floor(8.5, 19.0, "device X: LOD 2.83 ppm (Zr) over bg 0"),
        [("bottle", "Y")] = new Floor(6.0, 20.0, "device X: LOD 2.0 ppm (Y) over bg 0"),
    };

    // The operator-entered metal loadings (mass fraction of the element in the compound). Feed OrderAmount
    // only; the agent never sees them, because the order amount is not a judgment.
    private static Dictionary<string, double> Loadings() => new()
    {
        ["cas-zr"] = 0.74, ["cas-y"] = 0.787,
    };

    private static VerdictDoc Verdict(string cas, string comp, string element) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1",
        Cas = cas, ComponentId = comp, Element = element, Form = "form",
        Determination = Determinations.Recommended, DeterminationReason = "operator recommended",
        EvidenceReviewed = true,
    };

    // The compliant set the operator signed: Zr and Y, both in the bottle.
    private static List<VerdictDoc> Compliant() =>
        [Verdict("cas-zr", "bottle", "Zr"), Verdict("cas-y", "bottle", "Y")];

    private static DosingWindowOutput Window(
        string cas, string element, double recommended, double upper,
        string comp = "bottle", string upperKind = BoundKinds.Estimate, double quant = 15.0) =>
        new()
        {
            ComponentId = comp, Cas = cas, Element = element,
            RecommendedPpm = recommended, QuantificationPpm = quant,
            UpperPpm = upper, UpperBasis = "formulation impact", UpperKind = upperKind, UpperConfidence = 0.7,
            Rationale = "r",
        };

    private static DosingCodeOutput Code(string comp, params string[] cas) =>
        new() { ComponentId = comp, Cas = cas, Rationale = "code" };

    // A wholly well-formed output: two windows dosed above their floors, one 2-marker code. Zr@20, Y@10 —
    // reused by the RunAsync tests, which is why the ppms are exactly these (they imply "Zr:Y = 1.00:0.50").
    private static DosingOutput WellFormed() => new()
    {
        Windows = [Window("cas-zr", "Zr", 20, 100), Window("cas-y", "Y", 10, 100)],
        Codes = [Code("bottle", "cas-zr", "cas-y")],
    };

    // What a ScriptedAgent replies with — the well-formed output, on the wire. Zr@20, Y@10, one 2-marker code.
    private const string Valid = """
    { "windows": [
        { "componentId": "bottle", "cas": "cas-zr", "element": "Zr", "recommendedPpm": 20,
          "quantificationPpm": 19, "upperPpm": 100, "upperBasis": "formulation impact",
          "upperKind": "estimate", "upperConfidence": 0.7, "rationale": "dose well above floor" },
        { "componentId": "bottle", "cas": "cas-y", "element": "Y", "recommendedPpm": 10,
          "quantificationPpm": 20, "upperPpm": 100, "upperBasis": "formulation impact",
          "upperKind": "estimate", "upperConfidence": 0.7, "rationale": "second marker" } ],
      "codes": [ { "componentId": "bottle", "cas": ["cas-zr", "cas-y"], "rationale": "2-marker code" } ] }
    """;

    // ---- a well-formed output validates -------------------------------------------------------------

    [Fact]
    public void WellFormedOutput_ReturnsNull()
    {
        Assert.Null(DosingAgent.Validate(WellFormed(), Floors(), Compliant()));
    }

    // ---- invariant 1: a window outside the compliant set has no computed floor ----------------------

    [Fact]
    public void Window_WithNoComputedFloor_IsRejected()
    {
        // Cd is not in Floors() — the dispatcher only computes floors for the compliant set, so a window for
        // a substance outside it has nothing to dose above.
        var output = new DosingOutput { Windows = [Window("cas-cd", "Cd", 20, 100)] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("no floor was computed", error);
        Assert.Contains("Cd", error);
    }

    // ---- a window's element must match the compliant verdict's element for that CAS ------------------

    [Fact]
    public void Window_WithAnElementMislabelledAgainstTheCompliantVerdict_IsRejected()
    {
        // The compliant verdict for cas-y assigns element Y. The model emits a window for cas-y claiming Zr —
        // and a Zr floor EXISTS in Floors(), so invariant 1 (the floor lookup by (component, element)) finds a
        // floor and does NOT fire, and the below-floor check (invariant 2) would then measure the ppm against
        // Zr's floor (8.5) instead of Y's (6.0). If Y's true floor were the higher one, the marker could pass
        // while dosed below its real detection floor — the headline harm, uncaught. The (CAS → Element) map is
        // authoritative in the signed compliant set, so the mislabel must be caught here, naming both elements.
        //
        // The fixture violates ONLY this invariant: a Zr floor is present (invariant 1 cannot be what fails),
        // 20 ppm is above Zr's 8.5 floor (invariant 2 cannot fire), inside (8.5, 100) (invariant 3), and the
        // upper is an estimate (invariant 4). Strip the cross-check and this output validates clean — which is
        // the whole point of the fixture.
        var output = new DosingOutput { Windows = [Window("cas-y", "Zr", 20, 100)] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("cas-y", error);
        Assert.Contains("'Zr'", error);   // the claimed (wrong) element
        Assert.Contains("'Y'", error);    // the authoritative element from the compliant verdict
    }

    // ---- invariant 2: a recommended ppm at or below the detection floor (THE HEADLINE HARM) ----------

    [Fact]
    public void RecommendedPpm_AtOrBelowTheDetectionFloor_IsRejected()
    {
        // Zr's floor is 8.5; recommending 5 dooms the marker to be unreadable in the field. 5 < the upper
        // bound and the kind is legal, so ONLY the below-floor invariant can be at fault.
        var output = new DosingOutput { Windows = [Window("cas-zr", "Zr", 5, 100)] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("detection floor", error);
        Assert.Contains("8.5", error);   // the floor value is named
    }

    // ---- invariant 3: a recommended ppm at or above the upper bound ---------------------------------

    [Fact]
    public void RecommendedPpm_AtOrAboveTheUpperBound_IsRejected()
    {
        // 50 is above the 8.5 floor (passes invariant 2) but at/above the 30 upper — outside (floor, upper).
        var output = new DosingOutput { Windows = [Window("cas-zr", "Zr", 50, 30)] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("at or above the upper bound", error);
    }

    // ---- invariant 4: the agent may not label an upper bound "measured" -----------------------------

    [Fact]
    public void UpperBound_LabelledMeasured_IsRejected()
    {
        // 20 sits cleanly inside (8.5, 100), so only the kind can be wrong. "measured" is the one kind an
        // agent may never assert — it is the physicist's data alone.
        var output = new DosingOutput { Windows = [Window("cas-zr", "Zr", 20, 100, upperKind: BoundKinds.Measured)] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("may not assert", error);
        Assert.Contains("physicist", error);
        Assert.Contains(BoundKinds.Measured, error);
    }

    // ---- invariant 5: a code is 2-3 markers ---------------------------------------------------------

    [Fact]
    public void Code_WithOneMarker_IsRejected()
    {
        // One marker has no ratio to take. The windows are the well-formed pair, so only the code arity is
        // at fault.
        var output = new DosingOutput { Windows = WellFormed().Windows, Codes = [Code("bottle", "cas-zr")] };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("2-3 markers", error);
        Assert.Contains("has 1", error);
    }

    [Fact]
    public void Code_WithFourMarkers_IsRejected()
    {
        // Four is beyond what a field reader can resolve. The arity check fires before any per-marker lookup,
        // so the two extra (window-less) CASes never matter.
        var output = new DosingOutput
        {
            Windows = WellFormed().Windows,
            Codes = [Code("bottle", "cas-zr", "cas-y", "cas-a", "cas-b")],
        };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("2-3 markers", error);
        Assert.Contains("has 4", error);
    }

    // ---- invariant 6: a code CAS not in the compliant set (THE FALSE-PASS GUARD) --------------------

    [Fact]
    public void Code_WithACasNotInTheCompliantSet_IsRejected()
    {
        // cas-x was never recommended — a code goes to procurement, so this is a rejected substance trying to
        // ride past the regulatory gate. cas-zr is fully valid, isolating the fault to cas-x.
        var output = new DosingOutput
        {
            Windows = [Window("cas-zr", "Zr", 20, 100)],
            Codes = [Code("bottle", "cas-zr", "cas-x")],
        };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("not in the compliant set", error);
        Assert.Contains("cas-x", error);
    }

    // ---- invariant 7: codes are per component -------------------------------------------------------

    [Fact]
    public void Code_WithACasRecommendedForAnotherComponent_IsRejected()
    {
        // cas-y IS recommended (passes invariant 6) — but for the lid, and this code is for the bottle. There
        // is no product-wide marker.
        var compliant = new List<VerdictDoc> { Verdict("cas-zr", "bottle", "Zr"), Verdict("cas-y", "lid", "Y") };
        var output = new DosingOutput
        {
            Windows = [Window("cas-zr", "Zr", 20, 100), Window("cas-y", "Y", 10, 100)],
            Codes = [Code("bottle", "cas-zr", "cas-y")],
        };
        var error = DosingAgent.Validate(output, Floors(), compliant);
        Assert.NotNull(error);
        Assert.Contains("per component", error);
        Assert.Contains("cas-y", error);
        Assert.Contains("lid", error);
    }

    // ---- invariant 8: every code marker needs a dosable window --------------------------------------

    [Fact]
    public void Code_WithACasThatHasNoWindow_IsRejected()
    {
        // cas-y is compliant in the bottle (passes 6 and 7) but the model emitted no window for it, so there
        // is no ppm to dose it at.
        var output = new DosingOutput
        {
            Windows = [Window("cas-zr", "Zr", 20, 100)],
            Codes = [Code("bottle", "cas-zr", "cas-y")],
        };
        var error = DosingAgent.Validate(output, Floors(), Compliant());
        Assert.NotNull(error);
        Assert.Contains("no dosable ppm window", error);
        Assert.Contains("cas-y", error);
    }

    // ---- invariant 9: a code cannot carry two markers of the same element ---------------------------

    [Fact]
    public void Code_WithTwoMarkersOfTheSameElement_IsRejected()
    {
        // Two yttrium compounds. Every other check passes and RatioSignature would render it happily — but
        // XRF sees one combined Y peak and the code's identity is unrecoverable.
        var compliant = new List<VerdictDoc> { Verdict("cas-y1", "bottle", "Y"), Verdict("cas-y2", "bottle", "Y") };
        var output = new DosingOutput
        {
            Windows = [Window("cas-y1", "Y", 10, 100), Window("cas-y2", "Y", 12, 100)],
            Codes = [Code("bottle", "cas-y1", "cas-y2")],
        };
        var error = DosingAgent.Validate(output, Floors(), compliant);
        Assert.NotNull(error);
        Assert.Contains("two markers of the same element", error);
        Assert.Contains("Y", error);
    }

    // ---- RunAsync: CODE owns the arithmetic ---------------------------------------------------------

    [Fact]
    public async Task Run_ComputesTheRatioSignatureItself_NotFromTheModelsArithmetic()
    {
        // The model authored ppms (Zr@20, Y@10) and NO signature. The signature is derived from those ppms by
        // code — that it comes out "Zr:Y = 1.00:0.50" proves the identity is not the model's to state.
        var result = await DosingAgent.RunAsync(
            new ScriptedAgent(Valid), Constraints(), Compliant(), Floors(), Loadings(), null, default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Zr:Y = 1.00:0.50", Assert.Single(result.Output!.Codes).RatioSignature);
    }

    [Fact]
    public async Task Run_ComputesOrderAmountsItself_FromTheOperatorEnteredLoading()
    {
        // The order amounts are computed from the operator's metal loading (0.74 for Zr), never from the
        // model: element mass = ppm × batch mass; compound mass = element mass / loading.
        var result = await DosingAgent.RunAsync(
            new ScriptedAgent(Valid), Constraints(), Compliant(), Floors(), Loadings(), null, default);

        Assert.True(result.Succeeded, result.Error);
        var zr = Assert.Single(result.Output!.Codes).Markers.Single(m => m.Cas == "cas-zr");
        Assert.Equal(20.0 * 250.0, zr.ElementMassMg);              // ppm × batch mass
        Assert.Equal(20.0 * 250.0 / 0.74, zr.CompoundMassMg);      // element mass / loading
    }
}
