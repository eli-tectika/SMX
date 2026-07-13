using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// Revise-with-reason (Law 4) end to end through the dispatcher. These are the FALSE-PASS tests of the
/// whole feature: a revision replaces the analysis an operator's gate signature was taken over, and it
/// replaces the compliance artifact the operator reads. Either one left standing after a revise is
/// something unsafe silently wearing the appearance of "reviewed and current".
public class RevisionDispatchTests
{
    private const string P = "p1";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents, InMemoryKnowledgeStore Knowledge) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        // The REAL writer over fake dependencies: the conclusion has to land in Cosmos AND in the index,
        // and this is the seam where those two can drift apart.
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents, knowledge);
    }

    /// A project driven all the way through Regulatory and SIGNED: the operator opened the one verdict,
    /// ruled on it, and approved the regulatory gate — so the gate is `approved` and the Regulatory stage
    /// has reached `done`. This is precisely the state in which a revision is dangerous, because
    /// TryAssembleAsync will not lower a stage that already reached `done`.
    private static async Task SeedApprovedAsync(StageDispatcher d, InMemoryRecordStore store)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(project);
        await d.OnRecordChangedAsync(project, default);                                  // intake  → constraints
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync(P))!, default);    // discovery → candidates (Zr/cas-zr/bottle, tier A)
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync(P))!, default);     // regulatory fan-out → verdict + matrix

        var verdict = (await store.GetVerdictsAsync(P))[0];
        verdict.EvidenceReviewed = true;
        verdict.Determination = "recommended";
        await store.UpsertVerdictAsync(verdict);

        await store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-13T09:00:00.0000000+00:00",
        });
        await d.OnRecordChangedAsync((await store.GetGateAsync(P, GateTypes.Regulatory))!, default);

        Assert.Equal("done", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    private static RevisionDoc Revision(string stage, string reason, string? cas = null, string? componentId = null) => new()
    {
        Id = RecordIds.Revision(P, stage, "rev1"), ProjectId = P, Stage = stage,
        Target = "Zr neodecanoate (cas-zr) on bottle",
        Reason = reason, Cas = cas, ComponentId = componentId,
        CreatedAt = "2026-07-13T10:00:00.0000000+00:00",
    };

    private static CandidatesDoc Candidates(params CandidateSubstance[] substances) =>
        new() { Id = RecordIds.Candidates(P), ProjectId = P, Substances = [.. substances] };

    private static CandidateSubstance Substance(string tier, string form = "neodecanoate", string cas = "cas-zr") =>
        new("bottle", "Zr", form, cas, null, null, true, tier, "revised", [new Citation("catalog", "ref-catalog/x", "t")]);

    [Fact]
    public async Task Revise_VoidsAnApprovedRegulatoryGate_AndReopensTheStage()
    {
        // THE false-pass regression. A gate is the operator's signature over a SPECIFIC analysis. The
        // revision re-runs the agent and produces brand-new, UNREVIEWED output — so the signature no longer
        // covers what it appears to cover. Left standing, an approved gate + a `done` Regulatory stage would
        // silently absorb verdicts the operator never saw (TryAssembleAsync refuses to lower a `done` stage).
        // Void the signature and make them sign again. That friction IS the feature.
        var (d, store, _, _) = Sut();
        await SeedApprovedAsync(d, store);

        await d.OnRecordChangedAsync(Revision(Stages.Discovery, "Zr overlaps the Ti K-beta line in this matrix"), default);

        var gate = (await store.GetGateAsync(P, GateTypes.Regulatory))!;
        Assert.Equal("locked", gate.Status);
        Assert.Null(gate.ApprovedAt);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task Revise_Discovery_ReRunsTheAgentWithTheRevision_AndReplacesTheCandidates()
    {
        var (d, store, agents, _) = Sut();
        await SeedApprovedAsync(d, store);

        RevisionDoc? seenByAgent = null;
        agents.Discovery = (_, revision) =>
        {
            seenByAgent = revision;
            return Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(Candidates(Substance("B", form: "octoate"))));
        };

        var revision = Revision(Stages.Discovery, "prefer the octoate — the neodecanoate bleeds in HDPE");
        await d.OnRecordChangedAsync(revision, default);

        // The agent is not merely re-run: it is re-run WITH the operator's directive. A re-run that ignored
        // the reason would produce the same output and quietly discard the operator's instruction.
        Assert.NotNull(seenByAgent);
        Assert.Equal("prefer the octoate — the neodecanoate bleeds in HDPE", seenByAgent!.Reason);
        Assert.Equal(revision.Id, seenByAgent.Id);

        var candidates = (await store.GetCandidatesAsync(P))!;
        var substance = Assert.Single(candidates.Substances);
        Assert.Equal("octoate", substance.Form);     // replaced, not appended
        Assert.Equal("B", substance.Tier);
    }

    [Fact]
    public async Task Revise_Regulatory_ClearsTheOperatorsReviewOnTheReRunVerdict()
    {
        // A fresh verdict is fresh EVIDENCE. The operator's prior ruling ("recommended", evidence reviewed)
        // was made against the verdict this one replaces, so it cannot carry over — RegulatoryGate.Armable
        // must block the gate until the operator opens this item again.
        var (d, store, agents, knowledge) = Sut();
        await SeedApprovedAsync(d, store);
        var before = (await store.GetVerdictsAsync(P))[0];
        Assert.True(before.EvidenceReviewed);
        Assert.Equal("recommended", before.Determination);

        CandidateSubstance? targeted = null;
        agents.Regulatory = (c, cand, _) =>
        {
            targeted = cand;
            return Task.FromResult(AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
            {
                Id = RecordIds.Verdict(P, cand.Cas, cand.ComponentId), ProjectId = P,
                Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
                Dimensions = [new("ApplicationCheck", VerdictStatus.Conditional,
                    [new Citation("regulatory", "reach-annex-xvii", "t")], 0.8, "food-contact limits apply")],
            }));
        };

        var revision = Revision(Stages.Regulatory, "food-contact use was missed — re-screen against the FCM list",
            cas: "cas-zr", componentId: "bottle");
        await d.OnRecordChangedAsync(revision, default);

        Assert.Equal("cas-zr", targeted!.Cas);      // the revision resolved to the candidate it names
        var after = (await store.GetVerdictAsync(P, "cas-zr", "bottle"))!;
        Assert.False(after.EvidenceReviewed);
        Assert.Null(after.Determination);
        Assert.Null(after.DeterminationReason);
        Assert.Equal(VerdictStatus.Conditional, after.Overall);

        // Kind is CODE-derived from the stage, never the agent's: a regulatory revision is filed as a
        // regulatory judgment, where a regulatory reader will actually look for it.
        Assert.NotNull(await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.RegulatoryJudgment, revision.Id));
    }

    [Fact]
    public async Task Revise_WritesALearnedConclusion_WithTheOperatorsReasonVerbatimInProvenance()
    {
        const string reason = "barium sulfate overlaps the Ti K-beta line at 4.93 keV in this matrix";
        var (d, store, _, knowledge) = Sut();
        await SeedApprovedAsync(d, store);

        var revision = Revision(Stages.Discovery, reason);
        await d.OnRecordChangedAsync(revision, default);

        var conclusion = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, revision.Id);
        Assert.NotNull(conclusion);
        Assert.Equal(KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, revision.Id), conclusion!.Id);
        Assert.Equal(KnowledgeKinds.Material, conclusion.Kind);          // code-derived from the stage
        Assert.Equal([P], conclusion.Provenance.SourceProjects);

        // The operator's reason must reach the knowledge layer WORD FOR WORD. Provenance is code-owned for
        // exactly this: a model permitted to paraphrase "overlaps the Ti K-beta line" into "improved tiering"
        // would erase the only part of the record worth keeping.
        var decision = Assert.Single(conclusion.Provenance.Decisions);
        Assert.Contains(reason, decision);
        Assert.Contains(revision.Id, decision);
        Assert.Contains(revision.Target, decision);

        var applied = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.Equal(conclusion.Id, applied.ConclusionId);
        Assert.NotNull(applied.AppliedAt);
        Assert.Null(applied.Error);
    }

    [Fact]
    public async Task Revise_WhenTheDistillerFails_StillRecordsTheOperatorsReasonVerbatim()
    {
        // The distiller is a QUALITY step, never the source of truth. If it cannot produce a valid
        // conclusion we still record the operator's reason rather than dropping it — silently discarding the
        // "why" would break Law 4's promise that every change-with-a-reason teaches the system something.
        const string reason = "the ZnO form is out — the client's supplier cannot source it below 3um";
        var (d, store, agents, knowledge) = Sut();
        await SeedApprovedAsync(d, store);
        agents.Conclusion = (_, _, _) => Task.FromResult(
            AgentRunResult<ConclusionOutput>.NeedsReview("the distiller could not produce a valid conclusion"));

        var revision = Revision(Stages.Discovery, reason);
        await d.OnRecordChangedAsync(revision, default);

        var conclusion = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, revision.Id);
        Assert.NotNull(conclusion);
        Assert.Contains(reason, conclusion!.Finding);                     // verbatim, in place of the distillation
        Assert.Contains(reason, Assert.Single(conclusion.Provenance.Decisions));

        // The revision itself still APPLIED: the agent re-ran and the stage output was replaced. A failed
        // distillation must not roll that back or the operator's change would look un-applied.
        var applied = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.Equal(conclusion.Id, applied.ConclusionId);
    }

    [Fact]
    public async Task Revise_IsIdempotent_UnderChangeFeedRedelivery()
    {
        // The change feed is at-least-once, and marking the doc `applied` at the end re-enters the handler
        // once more by itself. A second application would re-run the agent and file a second conclusion for
        // one operator decision.
        var (d, store, agents, _) = Sut();
        await SeedApprovedAsync(d, store);
        var discoveryCallsBefore = agents.DiscoveryCalls;

        await d.OnRecordChangedAsync(Revision(Stages.Discovery, "drop the neodecanoate"), default);
        await d.OnRecordChangedAsync(Assert.Single(await store.GetRevisionsAsync(P)), default);   // redelivery (now `applied`)

        Assert.Equal(discoveryCallsBefore + 1, agents.DiscoveryCalls);
        Assert.Equal(1, agents.ConclusionCalls);
    }

    [Fact]
    public async Task Revise_WhenTheAgentCannotApplyIt_LeavesTheStageOutputIntact_AndMarksTheRevisionFailed()
    {
        var (d, store, agents, knowledge) = Sut();
        await SeedApprovedAsync(d, store);
        agents.Discovery = (_, _) => Task.FromResult(
            AgentRunResult<CandidatesDoc>.NeedsReview("no catalog hits for the requested form"));

        await d.OnRecordChangedAsync(Revision(Stages.Discovery, "use the octoate instead"), default);

        var failed = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("no catalog hits for the requested form", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Null(failed.AppliedAt);

        // Nothing was learned, because nothing was decided — a conclusion here would file the operator's
        // reason as accumulated knowledge for a change that never landed.
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal(0, agents.ConclusionCalls);

        // The prior analysis is untouched — and so, therefore, is the signature over it. The gate is voided
        // only when the output it covers is actually REPLACED.
        var substance = Assert.Single((await store.GetCandidatesAsync(P))!.Substances);
        Assert.Equal("A", substance.Tier);
        Assert.Equal("neodecanoate", substance.Form);
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
        Assert.Equal("done", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public void Router_RoutesARevisionDoc()
    {
        // The revision only reaches the dispatcher if the router knows the discriminator. Miss this and the
        // whole feature is inert: the doc is written, nothing runs, and the operator sees a silent no-op.
        var json = JsonSerializer.SerializeToElement(
            Revision(Stages.Discovery, "overlaps the Ti K-beta line"), Smx.Domain.Json.Options);

        var routed = RecordDocRouter.Route(json);

        var revision = Assert.IsType<RevisionDoc>(routed);
        Assert.Equal(Stages.Discovery, revision.Stage);
        Assert.Equal("overlaps the Ti K-beta line", revision.Reason);
        Assert.Equal(RevisionStatus.Pending, revision.Status);
    }
}
