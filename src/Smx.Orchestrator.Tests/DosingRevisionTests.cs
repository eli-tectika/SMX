using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// Revise-with-reason for DOSING (Law 4). Task 8 opened the two front doors (POST /revise and the chat
/// apply_revision tool) by making RevisionEffects.IsRevisable(Dosing) true; this file pins the executor arm
/// that closes the accept-then-fail window — before it, OnRevisionAsync's switch threw "not revisable" for
/// Dosing, so a Dosing revision was accepted then asynchronously failed. It also pins the two properties that
/// make a Dosing revision SAFE: it re-resolves the same inputs the first run enforced (so a directive cannot
/// dose outside the compliant set or below the floor), and it does NOT void the regulatory gate — Dosing is
/// downstream of that signature, so re-signing for it would train rubber-stamping.
public class DosingRevisionTests
{
    private const string P = "p1";
    private const string SeededGeneratedAt = "2020-01-01T00:00:00.0000000+00:00";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents, InMemoryKnowledgeStore Knowledge) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        // The REAL writer over fake dependencies: the Learned Conclusion has to land in the knowledge store
        // (Cosmos) AND in the index. The same real IKnowledgeStore is passed as the optional trailing param,
        // so Dosing can resolve the metal loading — the production wiring Task 12 deferred is exactly this arg.
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2, knowledge), store, agents, knowledge);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object round-tripped through the real
    /// router, never the instance the test is still holding.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    // ---- fixtures --------------------------------------------------------------------------------------

    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
        MeasuredBackgrounds = [new("bottle", "Zr", 5.0, "ppm")],
        Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]),
    };

    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new("bottle", "Zr", "neodecanoate", "cas-ok", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
            new("bottle", "Ba", "sulfate", "cas-no", null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]),
        ],
    };

    private static VerdictDoc Verdict(string cas, string element, VerdictStatus status, bool reviewed, string? determination) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
        Cas = cas, ComponentId = "bottle", Element = element, Form = "form",
        Dimensions = [new("ElementGate", status, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = reviewed,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
    };

    private static GateDoc Gate(string status) => new()
    {
        Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
        Status = status, ApprovedAt = status == "approved" ? "2026-07-13T09:00:00.0000000+00:00" : null,
    };

    private static SubstancePropertyDoc Loading(string cas, string element) => new()
    {
        Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = "form",
        MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    /// The Dosing output the operator is about to revise — a distinctive GeneratedAt + a single window, so a
    /// "nothing persisted" assertion can prove the doc is UNCHANGED rather than merely present.
    private static DosingDoc ExistingDosing() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P,
        Windows = [new PpmWindow("bottle", "cas-ok", "Zr",
            Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
            Upper: new Bound(900.0, "solubility", BoundKinds.Estimate, 0.4),
            RecommendedPpm: 450.0, QuantificationPpm: 20.0)],
        GeneratedAt = SeededGeneratedAt,
    };

    /// A project that has already been DOSED: Regulatory signed and `done`, exactly one compliant substance
    /// (cas-ok recommended; cas-no rejected), the floor's inputs on file, the loading known, and a DosingDoc
    /// already on the bus. This is the state a Dosing revision acts on.
    private static async Task SeedDosedAsync(InMemoryRecordStore store, InMemoryKnowledgeStore knowledge)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "done";   // signed AND promoted
        project.Stages[Stages.Matrix].Status = "done";
        project.Stages[Stages.Dosing].Status = "done";       // already dosed
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict("cas-ok", "Zr", VerdictStatus.Pass, reviewed: true, Determinations.Recommended));
        await store.UpsertVerdictAsync(Verdict("cas-no", "Ba", VerdictStatus.Pass, reviewed: true, Determinations.Rejected));
        await store.UpsertGateAsync(Gate("approved"));
        await store.UpsertDosingAsync(ExistingDosing());
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-ok", "Zr"));
    }

    private static RevisionDoc DosingRevision(string reason) => new()
    {
        Id = RecordIds.Revision(P, Stages.Dosing, "rev1"), ProjectId = P, Stage = Stages.Dosing,
        Target = "Zr neodecanoate (cas-ok) on bottle — raise the quantification floor",
        Reason = reason,
        CreatedAt = "2026-07-15T10:00:00.0000000+00:00",
    };

    private static StageState Stage(InMemoryRecordStore store, string stage) =>
        store.Documents.OfType<ProjectDoc>().Single().Stages[stage];

    // ---- the executor arm ------------------------------------------------------------------------------

    [Fact]
    public async Task ReviseDosing_ReRunsTheAgentWithTheDirective_AndWritesALearnedConclusion()
    {
        // The whole point of the task: a Dosing revision re-runs the DOSING agent WITH the operator's reason
        // (not merely re-runs it — a re-run that dropped the reason would produce the same ppm and silently
        // discard the instruction), and the reason becomes a Learned Conclusion filed under the Dosing kind so
        // a future dosing run can find it.
        const string reason = "the line reader struggles below 35 ppm";
        var (d, store, agents, knowledge) = Sut();
        await SeedDosedAsync(store, knowledge);

        RevisionDoc? seenByAgent = null;
        agents.Dosing = (c, _, _, _, rev) =>
        {
            seenByAgent = rev;
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
                Windows = [new PpmWindow("bottle", "cas-ok", "Zr",
                    Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
                    Upper: new Bound(900.0, "solubility", BoundKinds.Estimate, 0.4),
                    RecommendedPpm: 450.0, QuantificationPpm: 35.0)],  // raised, per the directive
                GeneratedAt = "2026-07-15T11:00:00Z",
            }));
        };

        var revision = DosingRevision(reason);
        await d.OnRecordChangedAsync(Delivered(revision), default);

        // The agent saw the directive.
        Assert.Equal(1, agents.DosingCalls);
        Assert.NotNull(seenByAgent);
        Assert.Equal(reason, seenByAgent!.Reason);

        // The reason became a Learned Conclusion, filed under the DOSING kind (code-derived from the stage,
        // never the agent's — a dosing revision must be findable where a dosing reader looks for it).
        var conclusion = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Dosing, revision.Id);
        Assert.NotNull(conclusion);
        Assert.Equal(KnowledgeKinds.Dosing, conclusion!.Kind);
        Assert.Equal(KnowledgeIds.RevisionConclusion(KnowledgeKinds.Dosing, revision.Id), conclusion.Id);
        Assert.Contains(reason, Assert.Single(conclusion.Provenance.Decisions));   // verbatim, code-owned

        // The revision APPLIED, and the re-run's output actually landed (35 ppm replaces the seeded 20).
        var applied = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.Equal(conclusion.Id, applied.ConclusionId);
        Assert.NotNull(applied.AppliedAt);
        Assert.Null(applied.Error);
        Assert.Equal(35.0, Assert.Single((await store.GetDosingAsync(P))!.Windows).QuantificationPpm);
    }

    [Fact]
    public async Task ReviseDosing_DoesNOTVoidTheRegulatoryGate()
    {
        // Dosing is DOWNSTREAM of the regulatory gate: it consumes the compliant set the operator signed over,
        // it cannot change it. So re-running it must NOT void that signature — RevisionEffects
        // .BreaksRegulatoryGate(Dosing) is false, and VoidRegulatoryGateAsync early-returns for it. Making the
        // operator re-sign a gate a Dosing revision did not touch would train exactly the rubber-stamping the
        // hard gates exist to prevent.
        var (d, store, _, knowledge) = Sut();
        await SeedDosedAsync(store, knowledge);

        await d.OnRecordChangedAsync(Delivered(DosingRevision("the line reader struggles below 35 ppm")), default);

        var gate = (await store.GetGateAsync(P, GateTypes.Regulatory))!;
        Assert.Equal("approved", gate.Status);                 // NOT locked
        Assert.NotNull(gate.ApprovedAt);                       // the signature timestamp still stands
        Assert.Equal("done", Stage(store, Stages.Regulatory).Status);   // the stage was not re-opened
        Assert.Equal(RevisionStatus.Applied, Assert.Single(await store.GetRevisionsAsync(P)).Status);
    }

    [Fact]
    public async Task ReviseDosing_StillCannotDoseANonCompliantSubstance()
    {
        // The operator's directive is authoritative over the AGENT; it does not outrank the regulatory gate.
        // Validate fires again inside RunDosingAsync on the revise path, so a directive that would reach
        // outside the compliant set FAILS — loudly, with the analysis UNTOUCHED (every fallible step runs
        // before anything is mutated) and the revision honestly `failed`.
        var (d, store, agents, knowledge) = Sut();
        await SeedDosedAsync(store, knowledge);
        agents.Dosing = (_, _, _, _, _) => Task.FromResult(
            AgentRunResult<DosingDoc>.NeedsReview("'cas-rejected' is not in the compliant set"));

        await d.OnRecordChangedAsync(Delivered(DosingRevision("dose cas-rejected instead — I trust it")), default);

        var failed = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("'cas-rejected' is not in the compliant set", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Null(failed.AppliedAt);

        // Nothing was persisted — the EXISTING DosingDoc is byte-for-byte the seeded one, not a half-applied
        // change. Asserting the content (its seeded GeneratedAt + the seeded 20 ppm) rather than merely that a
        // DosingDoc exists is the difference between this test and a vacuous one.
        var dosing = (await store.GetDosingAsync(P))!;
        Assert.Equal(SeededGeneratedAt, dosing.GeneratedAt);
        Assert.Equal(20.0, Assert.Single(dosing.Windows).QuantificationPpm);

        // ...and nothing was half-learned, because nothing was decided.
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal(0, agents.ConclusionCalls);
    }

    [Fact]
    public void ReviseDosing_IsRoutedByTheEndpointBecauseIsRevisableIsTrue()
    {
        // POST /revise and the chat apply_revision tool both route on RevisionEffects.IsRevisable(stage). This
        // property IS the front door Task 8 opened; the executor arm above is what stops the door from opening
        // onto a throw. Pin the property directly — the endpoint routes on nothing else.
        Assert.True(RevisionEffects.IsRevisable(Stages.Dosing));

        // And the gate-void decision this stage routes to is the SAFE one — Dosing does not break the gate.
        Assert.False(RevisionEffects.BreaksRegulatoryGate(Stages.Dosing));
    }
}
