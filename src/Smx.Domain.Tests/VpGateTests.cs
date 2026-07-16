using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// When may the VP gate be signed at all (spec §4 gates table: "Regulatory cleared + all components have a
/// selected code"). "Selected" at ARM time means the DecisionDoc OFFERS a code per component — a proposal
/// present; the VP's confirmation happens IN the signing call, never here. The predicate takes only records.
public class VpGateTests
{
    private static GateDoc Gate(string status) => new()
    {
        Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
        GateType = GateTypes.Regulatory, Status = status,
        ApprovedAt = status == "approved" ? "2026-07-16T00:00:00.0000000+00:00" : null,
    };

    private static ComponentDecision Component(string id, bool proposed) => new(
        id,
        Rows:
        [
            new DecisionRow("cas-zr", "Zr", Determinations.Recommended, 450.0,
                Cleared: new ClearedCriteria(Regulatory: true, Dosing: true, Cost: true),
                Traceability: new TraceRefs(
                    Verdict: RecordIds.Verdict("p1", "cas-zr", id),
                    Window: RecordIds.Dosing("p1"), Audit: RecordIds.Cost("p1"))),
        ],
        ProposedCode: proposed
            ? new ProposedCode("Zr:Y = 1.00:0.44", ["cas-zr", "cas-y"], "covers both criteria at lowest cost")
            : null);

    private static DecisionDoc Decision(params ComponentDecision[] components) => new()
    {
        Id = RecordIds.Decision("p1"), ProjectId = "p1", GeneratedAt = "t",
        Components = [.. components],
    };

    [Fact]
    public void Armable_WhenRegulatoryApproved_AndEveryComponentHasAProposal()
    {
        var (ok, blockers) = VpGate.Armable(
            Gate("approved"), Decision(Component("bottle", proposed: true), Component("label", proposed: true)));

        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void NotArmable_WithoutTheRegulatorySignature()
    {
        // A VP signature over an unsigned compliance analysis would stack one gate on a void: the decision
        // matrix was assembled FROM the compliant set the regulatory signature vouches for. A locked gate
        // and an absent gate block identically — neither is a signature.
        foreach (var regGate in new[] { Gate("locked"), null })
        {
            var (ok, blockers) = VpGate.Armable(regGate, Decision(Component("bottle", proposed: true)));

            Assert.False(ok);
            var blocker = Assert.Single(blockers);
            Assert.Equal("regulatory gate is not approved", blocker);
        }
    }

    [Fact]
    public void NotArmable_WhenAComponentHasNoProposedCode()
    {
        // The blocker NAMES the component — "a component is missing a code" that names none sends the
        // operator hunting through every component to find which one blocked the gate.
        var (ok, blockers) = VpGate.Armable(
            Gate("approved"), Decision(Component("bottle", proposed: true), Component("label", proposed: false)));

        Assert.False(ok);
        var blocker = Assert.Single(blockers);
        Assert.Contains("label", blocker);
        Assert.DoesNotContain("bottle", blocker);
    }

    [Fact]
    public void NotArmable_WithNoDecisionDoc()
    {
        var (ok, blockers) = VpGate.Armable(Gate("approved"), null);

        Assert.False(ok);
        var blocker = Assert.Single(blockers);
        Assert.Equal("decision has not run", blocker);
    }

    [Fact]
    public void NotArmable_WhenTheDecisionCoversNoComponents()
    {
        // Zero components is unreachable today via the upstream guarantees (DecisionAssembler emits one
        // ComponentDecision per constraints component), but Armable is a STANDALONE predicate — and the
        // signing endpoint's confirm loop iterates decision.Components, so a zero-component decision would
        // otherwise arm a gate whose approval vacuously "confirmed" nothing. An empty decision is not a
        // decision; it must not be signable.
        var (ok, blockers) = VpGate.Armable(Gate("approved"), Decision());

        Assert.False(ok);
        var blocker = Assert.Single(blockers);
        Assert.Equal("decision covers no components", blocker);
    }
}
