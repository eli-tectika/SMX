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
    public void NotSignableBlocker_IsNullOnlyOverAFinishedProposal()
    {
        // Was ParkBlocker_IsNullOnlyWhileTheStageAwaitsTheVp. With the park deleted (execution-core §8) the
        // signable state is `done` — the Decision agent finished and there is a proposal on file. The gate
        // read + dashboard surface the SAME blocker, so no affordance advertises a POST that would 422.
        Assert.Null(VpGate.NotSignableBlocker("done", ProcurementStatus.Unreleased));

        foreach (var status in new[] { "pending", "running", "needs-review", "failed" })
        {
            var blocker = VpGate.NotSignableBlocker(status, ProcurementStatus.Unreleased);
            Assert.NotNull(blocker);
            Assert.Contains($"'{status}'", blocker);        // names the actual status the operator sees
            Assert.Contains("not 'done'", blocker);         // and the one a signature answers
        }

        // No project / no stage record is not a finished proposal either.
        Assert.NotNull(VpGate.NotSignableBlocker(null, ProcurementStatus.Unreleased));
        Assert.Contains("not 'done'", VpGate.NotSignableBlocker(null, ProcurementStatus.Unreleased));
    }

    [Fact]
    public void NotSignableBlocker_RefusesAClosedProject_EvenThoughItsStageReadsDone()
    {
        // THE HALF THE STAGE STATUS CAN NO LONGER CARRY. `done` used to mean "the VP signed and the project
        // closed", so refusing history was the same check as refusing a draft. Now `done` only means the
        // agent finished, and a closed project sits at `done` too — so the post-close refusal is read from
        // procurement, which moves Unreleased -> Released exactly once.
        //
        // Without this, an approve would rewrite a signed history and a REJECT would flip the gate locked
        // over Released procurement: a revocation that revokes nothing.
        var blocker = VpGate.NotSignableBlocker("done", ProcurementStatus.Released);

        Assert.NotNull(blocker);
        Assert.Contains("closed", blocker);
    }

    private static RevisionDoc Revision(string stage, string status) => new()
    {
        Id = RecordIds.Revision("p1", stage, "r1"), ProjectId = "p1", Stage = stage,
        Target = "t", Reason = "why", Status = status,
        CreatedAt = "2026-07-16T10:00:00.0000000+00:00",
    };

    [Fact]
    public void PendingRevisionBlocker_BlocksWhileADosingOrDecisionRevisionIsPending()
    {
        // F1 layer 3: the revise run is minutes wide and the stage advertises `awaiting-VP` throughout —
        // but the RevisionDoc is durable from POST /revise's 202 until applied/failed, so it is the one
        // record that covers the whole window. A pending Dosing or Decision revision means the decision
        // may be about to change; the VP must not sign words that are being rewritten.
        foreach (var stage in new[] { Stages.Dosing, Stages.Decision })
        {
            var blocker = VpGate.PendingRevisionBlocker([Revision(stage, RevisionStatus.Pending)]);
            Assert.NotNull(blocker);
            Assert.Contains(stage, blocker);            // names the stage whose revision is in flight
            Assert.Contains("pending", blocker);
        }

        // A LANDED revision blocks nothing — applied re-parked the stage (the park guard takes over),
        // failed changed nothing.
        Assert.Null(VpGate.PendingRevisionBlocker([Revision(Stages.Dosing, RevisionStatus.Applied)]));
        Assert.Null(VpGate.PendingRevisionBlocker([Revision(Stages.Decision, RevisionStatus.Failed)]));
        Assert.Null(VpGate.PendingRevisionBlocker([]));

        // Upstream stages are deliberately NOT listed: a Discovery/Regulatory revision voids the
        // REGULATORY gate when it lands, and the determination's Armable + coverage re-check already
        // refuse an unsigned or uncovered analysis — that window has its own guards.
        Assert.Null(VpGate.PendingRevisionBlocker([Revision(Stages.Discovery, RevisionStatus.Pending)]));
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
