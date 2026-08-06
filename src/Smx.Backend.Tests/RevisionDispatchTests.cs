using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;
using Smx.Backend.Knowledge;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// Revise-with-reason (Law 4) end to end through the dispatcher. These are the FALSE-PASS tests of the
/// whole feature: a revision replaces the analysis an operator's gate signature was taken over, and it
/// replaces the compliance artifact the operator reads. Either one left standing after a revise is
/// something unsafe silently wearing the appearance of "reviewed and current".
public class RevisionDispatchTests
{
    private const string P = "p1";

    private static (PipelineRunner Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents, InMemoryKnowledgeStore Knowledge) Sut(
        ILearnedConclusionWriter? conclusions = null)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        // The REAL writer over fake dependencies: the conclusion has to land in Cosmos AND in the index,
        // and this is the seam where those two can drift apart.
        conclusions ??= new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(), conclusions, 2), store, agents, knowledge);
    }

    /// Every real dependency behind ILearnedConclusionWriter can fail on day one: EnsureIndexAsync needs a
    /// role assignment that takes minutes to propagate after a deploy (403), the embedding deployment is
    /// provisioned at capacity 1 (429), and the distiller is the third consecutive LLM call on the same
    /// account. This is not a hypothetical fault — it is the expected first-revision failure.
    private sealed class ThrowingConclusionWriter(string message) : ILearnedConclusionWriter
    {
        public Task WriteAsync(LearnedConclusionDoc doc, CancellationToken ct) =>
            throw new InvalidOperationException(message);
    }

    /// A project driven all the way through Regulatory with the operator's ruling on file: they opened the
    /// one verdict and recommended it, and the Regulatory stage has reached `done`. This is precisely the
    /// state in which a revision is dangerous, because TryAssembleAsync will not lower a stage that already
    /// reached `done` — so a revision's fresh, unreviewed output can land under a stage that reads finished.
    ///
    /// It used to sign a regulatory gate here too. That gate is gone (§16.4); what carries the danger is the
    /// VERDICT's own EvidenceReviewed flag, which the Regulatory revision test below pins.
    private static async Task SeedApprovedAsync(PipelineRunner d, InMemoryRecordStore store)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(project);
        await d.RunAsync(P, default);   // intake → discovery → regulatory fan-out → matrix

        var verdict = (await store.GetVerdictsAsync(P))[0];
        verdict.EvidenceReviewed = true;
        verdict.Determination = Determinations.Recommended;
        await store.UpsertVerdictAsync(verdict);

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

    // Revise_VoidsAnApprovedRegulatoryGate_AndReopensTheStage was here. Its subject — the R.E.'s signature
    // being voided by a revision — is gone with the gate (§16.4), and there is no signature left for a
    // revision to void: ThrowIfClosedAsync refuses every revision on a project whose VP gate is approved
    // (ARevision_AfterClose_IsRefused, DecisionRevisionTests).
    //
    // The false pass it guarded — a `done` Regulatory stage silently absorbing verdicts the operator never
    // saw — is NOT unguarded. It moved onto the verdict itself, and
    // Revise_Regulatory_ClearsTheOperatorsReviewOnTheReRunVerdict below now asserts the whole chain:
    // the re-run verdict comes back unreviewed, and EvidenceReview.Outstanding names it.

    [Fact]
    public async Task Revise_Discovery_ReRunsTheAgentWithTheRevision_AndReplacesTheCandidates()
    {
        var (d, store, agents, _) = Sut();
        await SeedApprovedAsync(d, store);

        RevisionDoc? seenByAgent = null;
        ProjectDoc? projectSeenByAgent = null;
        agents.Discovery = (project, _, revision) =>
        {
            seenByAgent = revision;
            projectSeenByAgent = project;
            return Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(Candidates(Substance("B", form: "octoate"))));
        };

        var revision = Revision(Stages.Discovery, "prefer the octoate — the neodecanoate bleeds in HDPE");
        await d.OnRevisionAsync(revision, default);

        // The agent is not merely re-run: it is re-run WITH the operator's directive. A re-run that ignored
        // the reason would produce the same output and quietly discard the operator's instruction.
        Assert.NotNull(seenByAgent);
        Assert.Equal("prefer the octoate — the neodecanoate bleeds in HDPE", seenByAgent!.Reason);
        Assert.Equal(revision.Id, seenByAgent.Id);

        // The revise path is a SECOND Discovery run, with the same external-search reach as the first — so it
        // gets the same project terms. This call site is the one an ordinary-run-only test would miss.
        Assert.Equal("Acme", projectSeenByAgent!.Client);
        Assert.Equal("Bottle", projectSeenByAgent.Product);

        var candidates = (await store.GetCandidatesAsync(P))!;
        var substance = Assert.Single(candidates.Substances);
        Assert.Equal("octoate", substance.Form);     // replaced, not appended
        Assert.Equal("B", substance.Tier);
    }

    [Fact]
    public async Task Revise_Regulatory_ClearsTheOperatorsReviewOnTheReRunVerdict()
    {
        // A fresh verdict is fresh EVIDENCE. The operator's prior ruling ("recommended", evidence reviewed)
        // was made against the verdict this one replaces, so it cannot carry over.
        //
        // THIS IS WHAT REPLACED THE DELETED GATE-VOIDING STEP, and the last assertion is the load-bearing
        // one: clearing the flag would be pointless bookkeeping if nothing read it. EvidenceReview.Outstanding
        // reads it, and refuses the VP signature and POST /orders until the operator opens this item again.
        var (d, store, agents, knowledge) = Sut();
        await SeedApprovedAsync(d, store);
        var before = (await store.GetVerdictsAsync(P))[0];
        Assert.True(before.EvidenceReviewed);
        Assert.Equal(Determinations.Recommended, before.Determination);

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
        await d.OnRevisionAsync(revision, default);

        Assert.Equal("cas-zr", targeted!.Cas);      // the revision resolved to the candidate it names
        var after = (await store.GetVerdictAsync(P, "cas-zr", "bottle"))!;
        Assert.False(after.EvidenceReviewed);
        Assert.Null(after.Determination);
        Assert.Null(after.DeterminationReason);
        Assert.Equal(VerdictStatus.Conditional, after.Overall);

        // ...and the cleared flag actually BLOCKS something: the re-run verdict is a live non-Pass nobody
        // has opened, so the irreversible acts refuse over it by name.
        var unopened = EvidenceReview.Outstanding(
            (await store.GetCandidatesAsync(P))!, await store.GetVerdictsAsync(P));
        Assert.Contains("cas-zr", Assert.Single(unopened));

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
        await d.OnRevisionAsync(revision, default);

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
        await d.OnRevisionAsync(revision, default);

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

        await d.OnRevisionAsync(Revision(Stages.Discovery, "drop the neodecanoate"), default);
        await d.OnRevisionAsync(Assert.Single(await store.GetRevisionsAsync(P)), default);   // re-delivered, now `applied`

        Assert.Equal(discoveryCallsBefore + 1, agents.DiscoveryCalls);
        Assert.Equal(1, agents.ConclusionCalls);
    }

    [Fact]
    public async Task Revise_WhenTheAgentCannotApplyIt_LeavesTheStageOutputIntact_AndMarksTheRevisionFailed()
    {
        var (d, store, agents, knowledge) = Sut();
        await SeedApprovedAsync(d, store);
        agents.Discovery = (_, _, _) => Task.FromResult(
            AgentRunResult<CandidatesDoc>.NeedsReview("no catalog hits for the requested form"));

        await d.OnRevisionAsync(Revision(Stages.Discovery, "use the octoate instead"), default);

        var failed = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("no catalog hits for the requested form", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Null(failed.AppliedAt);

        // Nothing was learned, because nothing was decided — a conclusion here would file the operator's
        // reason as accumulated knowledge for a change that never landed.
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal(0, agents.ConclusionCalls);

        // The prior analysis is untouched.
        var substance = Assert.Single((await store.GetCandidatesAsync(P))!.Substances);
        Assert.Equal("A", substance.Tier);
        Assert.Equal("neodecanoate", substance.Form);
        Assert.Equal("done", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task Revise_RebuildsTheMatrix_RatherThanLeavingThePreRevisionOne()
    {
        // The matrix is the artifact the operator reads and the XLSX export ships. TryAssembleAsync used to
        // write it only when there wasn't one, so after a revise it kept showing the tiers and verdicts the
        // revision REPLACED — a stale compliance artifact that looks perfectly current, which is the single
        // most dangerous thing this system could hand someone.
        var (d, store, agents, _) = Sut();
        await SeedApprovedAsync(d, store);
        var before = (await store.GetMatrixAsync(P))!;
        Assert.Single(before.Cells);                                  // Zr is tier A ⇒ it has a screened cell

        // The revision drops Zr to tier C: it is no longer a screened candidate, so it must LEAVE the matrix.
        agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(Candidates(Substance("C"))));

        await d.OnRevisionAsync(Revision(Stages.Discovery, "Zr is tier C here — the bottle already contains it"), default);
        await d.RunAsync(P, default);   // the next pass re-assembles over the revised candidates

        var after = (await store.GetMatrixAsync(P))!;
        Assert.Empty(after.Cells);
        Assert.Empty(after.Rows);
        Assert.NotEqual(before.GeneratedAt, after.GeneratedAt);
    }

    [Fact]
    public async Task Revise_WhenTheConclusionWriteFails_LeavesTheAnalysisUNTOUCHED_AndMarksTheRevisionFailed()
    {
        // The conclusion write is the MOST failure-prone step in the whole revise path — a third consecutive
        // LLM call, a Cosmos upsert, an embedding call, a control-plane index create and a search push — and
        // it used to run LAST, after the candidates had been replaced and the gate voided. A 403 or a 429
        // there (both expected on day one) left the worst possible state: the revision terminally `failed`
        // (nothing ever retries it), the change nevertheless LIVE and permanent, the gate voided, and the
        // operator's reason — the one artifact in this system that exists in no corpus — gone forever. The
        // audit trail said `failed` about a mutation that had happened. Re-issuing then re-ran the agent over
        // ALREADY-REVISED output, so a relative directive ("lower it one tier") double-applied.
        //
        // Now every fallible step runs first, so a failure here mutates NOTHING.
        var (d, store, agents, knowledge) = Sut(new ThrowingConclusionWriter("search index create returned 403 (Forbidden)"));
        await SeedApprovedAsync(d, store);
        agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(Candidates(Substance("C"))));

        await d.OnRevisionAsync(Revision(Stages.Discovery, "Zr overlaps the Ti K-beta line in this matrix"), default);

        var failed = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("403", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Null(failed.AppliedAt);

        // The analysis is exactly as it was. The change did NOT land...
        var substance = Assert.Single((await store.GetCandidatesAsync(P))!.Substances);
        Assert.Equal("A", substance.Tier);                    // NOT the "C" the re-run produced
        Assert.Equal("neodecanoate", substance.Form);
        // ...the stage still stands over the analysis it actually describes...
        Assert.Equal("done", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
        // ...and nothing was half-learned. A `failed` revision the operator can simply re-issue.
        Assert.Empty(await knowledge.QueryLearnedConclusionsAsync(null));
    }

    // THREE MEMBERS LIVED HERE and all three are deleted:
    //   SeedSignedButNotYetPromotedAsync
    //   Assemble_RefusesToPromoteRegulatoryToDone_WhenTheApprovedGateNoLongerCoversTheVerdicts
    //   Assemble_PromotesRegulatoryToDone_WhenTheApprovedGateStillCoversTheVerdicts
    //
    // They asserted that the matrix pass DERIVED the Regulatory stage's status from the gate record plus its
    // arming predicate. That derivation was already gone before this change (execution-core §8 made the
    // stage say only "the agent ran"), and both tests had degenerated to asserting `done` on both sides of
    // their own premise — they could no longer fail for the reason they were named after. §16.4 removed the
    // gate they read, so there is nothing left to rewrite them onto.
    //
    // The false pass they were written for — a fresh unreviewed FAILING verdict landing under an existing
    // approval — is asserted where it now bites: OrderPreconditionTests
    // .ALateUnreviewedFailingVerdict_RefusesTheOrder, and the same check on POST /decision/determination.

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
