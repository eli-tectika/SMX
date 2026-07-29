using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Pipeline;
using Smx.Backend.Knowledge;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The ported StageDispatcherTests. Every assertion about STAGE BEHAVIOUR survives the rewrite; what is
/// gone is only the shape of the trigger — a record landing on a change feed is no longer what runs a
/// stage, so "a redelivered ConstraintsDoc must not re-run the pool agent" is now "a second pass must
/// not", which is the same property under the mechanism that replaced it.
public class PipelineRunnerTests
{
    private static (PipelineRunner Runner, InMemoryRecordStore Store, FakeAgentRuns Agents, InMemoryRunStore Runs)
        Sut(int parallelism = 2, bool autoApprove = false)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var runs = new InMemoryRunStore();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, runs, agents, new ThreadEventHub(), conclusions, parallelism,
                    regulatoryAutoApprove: autoApprove),
                store, agents, runs);
    }

    private static async Task<ProjectDoc> Seed(InMemoryRecordStore store)
    {
        var doc = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    private static GateDoc RegulatoryGateDoc(string status) => new()
    {
        Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
        GateType = GateTypes.Regulatory, Status = status,
        ApprovedAt = status == "approved" ? "t" : null,
    };

    // REGULATORY_AUTO_APPROVE — the human gate off. The pipeline adopts the agent's PROPOSED determination,
    // marks every verdict reviewed, and signs the gate itself, so Regulatory reaches `done` (not awaiting-RE)
    // and the compliant set is populated — all with no operator. This is the flag's whole point; the contrast
    // (flag off ⇒ awaiting-RE, empty compliant set) is the default every other test in this file exercises.
    [Fact]
    public async Task RegulatoryAutoApprove_adopts_the_proposal_signs_the_gate_and_skips_the_human_park()
    {
        var (d, store, agents, _) = Sut(autoApprove: true);
        // The agent PROPOSES recommended; without the flag this would only pre-fill ProposedDetermination and
        // wait for the R.E. to confirm it.
        agents.Regulatory = (c, cand, _) => Task.FromResult(Smx.Backend.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            ProposedDetermination = Determinations.Recommended,
        }));
        await Seed(store);
        await d.RunAsync("p1", default);

        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.True(v.EvidenceReviewed));                       // auto-reviewed
        Assert.All(verdicts, v => Assert.Equal(Determinations.Recommended, v.Determination)); // proposal adopted
        Assert.Equal("approved", (await store.GetGateAsync("p1", GateTypes.Regulatory))?.Status); // gate self-signed
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status); // no awaiting-RE
        Assert.NotEmpty(CompliantSet.Of(verdicts));                                        // dosable set is non-empty
    }

    // A null proposal (the failed-verdict fallback) must NOT be auto-recommended — the safe asymmetry survives
    // even with the human gate off: an un-screenable substance is excluded, never signed through.
    [Fact]
    public async Task RegulatoryAutoApprove_leaves_a_null_proposal_undetermined()
    {
        var (d, store, agents, _) = Sut(autoApprove: true);
        agents.Regulatory = (c, cand, _) => Task.FromResult(Smx.Backend.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [], 0, "no cited verdict")],
            ProposedDetermination = null,
        }));
        await Seed(store);
        await d.RunAsync("p1", default);

        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.Null(v.Determination));   // not auto-recommended
        Assert.Empty(CompliantSet.Of(verdicts));                   // nothing dosable
    }

    // ---- the run trail --------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_the_stages_in_order_and_records_a_run_for_each()
    {
        var (d, store, _, runs) = Sut();
        await Seed(store);

        await d.RunAsync("p1", default);

        var stages = (await runs.ListAsync("p1", null, default)).Select(r => r.Stage).ToList();
        Assert.Equal(Stages.Intake, stages[0]);
        Assert.Contains(Stages.Discovery, stages);
        Assert.Contains(Stages.Regulatory, stages);
        Assert.Contains(Stages.Matrix, stages);
    }

    /// Resume is free because the skip is keyed on the OUTPUT DOC. A stage whose output is already on file
    /// is a stage that ran, whatever the process that ran it did next — and a skipped stage opens no run,
    /// so a resume leaves no empty group in the operator's timeline.
    [Fact]
    public async Task Skips_a_stage_whose_output_already_exists()
    {
        var (d, store, agents, runs) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        var intakeCalls = agents.IntakeCalls;
        var runsAfterFirst = (await runs.ListAsync("p1", Stages.Intake, default)).Count;

        await d.RunAsync("p1", default);

        Assert.Equal(intakeCalls, agents.IntakeCalls);
        Assert.Equal(runsAfterFirst, (await runs.ListAsync("p1", Stages.Intake, default)).Count);
    }

    /// A failure stops the pipeline. Carrying on would run Discovery over constraints that do not exist.
    [Fact]
    public async Task A_failed_stage_halts_the_pipeline_and_stamps_both_run_and_stage()
    {
        var (d, store, agents, runs) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await Seed(store);

        await d.RunAsync("p1", default);

        var all = await runs.ListAsync("p1", null, default);
        Assert.Single(all);
        Assert.Equal(RunOutcome.Failed, all[0].Outcome);
        Assert.Contains("foundry 500", all[0].Error);
        Assert.Equal("failed", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
        Assert.Equal(0, agents.DiscoveryCalls);
    }

    /// Operator cancel and host shutdown arrive at the same catch. Only one of them is a cancellation the
    /// operator asked for; the other must leave the stage resumable.
    [Fact]
    public async Task An_operator_cancel_stamps_cancelled()
    {
        var (d, store, agents, runs) = Sut();
        var entered = new TaskCompletionSource();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        agents.Intake = async _ =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, safety.Token);   // the runner's cancel is what aborts this
            return null!;
        };
        await Seed(store);
        using var host = new CancellationTokenSource();

        var task = d.RunAsync("p1", host.Token);
        await entered.Task;
        Assert.True(d.CancelRun(RunIds.Run("p1", Stages.Intake, 1)));
        await task;

        var all = await runs.ListAsync("p1", null, default);
        Assert.Equal(RunOutcome.Cancelled, all[0].Outcome);
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
        Assert.Contains("cancelled by the operator", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Error);
    }

    /// The other half of the same catch: a HOST shutdown is not a decision anyone made about this project.
    /// It re-throws, so the stage keeps `running` and a restart can pick it up — the alternative is a
    /// project that reads `cancelled` because a container was recycled.
    [Fact]
    public async Task A_host_shutdown_rethrows_and_leaves_the_stage_running()
    {
        var (d, store, agents, _) = Sut();
        var entered = new TaskCompletionSource();
        using var host = new CancellationTokenSource();
        agents.Intake = async _ =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, host.Token);
            return null!;
        };
        await Seed(store);

        var task = d.RunAsync("p1", host.Token);
        await entered.Task;
        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal("running", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    // ---- intake ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectCreated_RunsIntake_WritesConstraints_MarksStageDone()
    {
        var (d, store, agents, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Equal(1, agents.IntakeCalls);
        Assert.NotNull(await store.GetConstraintsAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task IntakeThrow_MarksStageFailed_WithErrorDetail()
    {
        var (d, store, agents, _) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await Seed(store);
        await d.RunAsync("p1", default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("failed", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("foundry 500", proj.Stages[Stages.Intake].Error);
    }

    /// The agent may CREATE a project; only the operator may START one (design §2.3). An interview-created
    /// project sits at `awaiting-confirmation` until Start Processing, and the runner must not make that
    /// call on the operator's behalf even if it is pointed at the project.
    [Fact]
    public async Task An_unconfirmed_project_does_not_run_intake()
    {
        var (d, store, agents, _) = Sut();
        await store.UpsertProjectAsync(ProjectDoc.Create(
            "p1", "Acme", "P", JsonDocument.Parse("{}").RootElement, StageStatus.AwaitingConfirmation));

        await d.RunAsync("p1", default);

        Assert.Equal(0, agents.IntakeCalls);
        Assert.Null(await store.GetConstraintsAsync("p1"));
        Assert.Equal(StageStatus.AwaitingConfirmation,
            (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    // ---- discovery ------------------------------------------------------------------------------------

    [Fact]
    public async Task ConstraintsWritten_RunsDiscovery_WritesCandidates()
    {
        var (d, store, agents, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.NotNull(await store.GetCandidatesAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    /// Constraints with NO element pool and NO provided candidates — the need-only path. Intake produces
    /// just the need (with the substrate's physical state); the pool agent proposes the pool.
    private static void NeedOnly(FakeAgentRuns agents) =>
        agents.Intake = p => Task.FromResult(Smx.Backend.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", null, "solid")],
            ElementPools = [],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    // The need-only journey: the operator enters only the need, so Intake writes a ConstraintsDoc with no
    // element pool. That must run the POOL agent BEFORE Discovery, drive Background (pass-through), and
    // hand Discovery the proposed pool mapped onto the constraints in memory.
    [Fact]
    public async Task NeedOnly_RunsPoolAgent_ThenBackgroundPassthrough_ThenDiscoveryOverTheProposedPool()
    {
        var (d, store, agents, _) = Sut();
        NeedOnly(agents);
        await Seed(store);
        ConstraintsDoc? handedToDiscovery = null;
        PoolDoc? poolWhenDiscoveryRan = null;
        var run = agents.Discovery;
        agents.Discovery = (pr, c, r) =>
        {
            handedToDiscovery = c;
            // ORDER, asserted from INSIDE Discovery rather than from an intermediate store read: pool and
            // discovery no longer happen in two separately-observable dispatches.
            poolWhenDiscoveryRan = store.GetPoolAsync("p1").GetAwaiter().GetResult();
            return run(pr, c, r);
        };

        await d.RunAsync("p1", default);

        Assert.Equal(1, agents.PoolCalls);
        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.NotNull(poolWhenDiscoveryRan);                    // the pool was on file BEFORE Discovery ran
        var p = (await store.GetProjectAsync("p1"))!;
        Assert.Equal("done", p.Stages[Stages.Pool].Status);
        Assert.Equal("done", p.Stages[Stages.Background].Status);
        Assert.Equal("done", p.Stages[Stages.Discovery].Status);
        Assert.NotNull(await store.GetCandidatesAsync("p1"));

        // Discovery was handed the proposed pool (mapped onto the in-memory constraints), not an empty one.
        Assert.NotNull(handedToDiscovery);
        Assert.Contains(handedToDiscovery!.ElementPools, e => e.Component == "bottle" && e.Element == "Zr");
        // ...and the PERSISTED constraints stay frozen: the map is in-memory only.
        Assert.Empty((await store.GetConstraintsAsync("p1"))!.ElementPools);
    }

    // A second pass over a project whose pool is already on file must not re-run the pool agent. (Was: an
    // at-least-once redelivery of the ConstraintsDoc.)
    [Fact]
    public async Task NeedOnly_ASecondPass_DoesNotRerunPoolAgent()
    {
        var (d, store, agents, _) = Sut();
        NeedOnly(agents);
        await Seed(store);
        await d.RunAsync("p1", default);
        await d.RunAsync("p1", default);
        Assert.Equal(1, agents.PoolCalls);
    }

    // Discovery is the only stage that can reach the public internet, and its web-search tool is built from
    // the ProjectDoc's client/product/project-id — the terms it must refuse to send. The runner is what
    // hands them over: a ConstraintsDoc carries neither name, so a Discovery run started without the
    // project is a run whose external search has nothing to protect.
    [Fact]
    public async Task Discovery_IsHandedTheProject_TheOnlyRecordCarryingTheTermsTheWebMustNotSee()
    {
        var (d, store, agents, _) = Sut();
        ProjectDoc? handedToDiscovery = null;
        var run = agents.Discovery;
        agents.Discovery = (p, c, r) => { handedToDiscovery = p; return run(p, c, r); };

        await Seed(store);
        await d.RunAsync("p1", default);

        Assert.NotNull(handedToDiscovery);
        Assert.Equal("p1", handedToDiscovery!.ProjectId);
        Assert.Equal("Acme", handedToDiscovery.Client);
        Assert.Equal("P", handedToDiscovery.Product);
    }

    /// Constraints carrying operator-supplied candidates. `cas` is a parameter because the CAS is the one
    /// thing this door has to check.
    private static void ProvideCandidates(FakeAgentRuns agents, string cas) =>
        agents.Intake = p => Task.FromResult(Smx.Backend.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ProvidedCandidates = [new("bottle", "Zr", "oxide", cas, null, null, true, "A", "provided",
                [new Citation("catalog", "x", "t")])],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    [Fact]
    public async Task ConstraintsWithProvidedCandidates_BypassesDiscoveryAgent()
    {
        var (d, store, agents, _) = Sut();
        ProvideCandidates(agents, "1314-23-4");                 // zirconium dioxide — a REAL, valid CAS
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Equal(0, agents.DiscoveryCalls);                 // bypassed
        Assert.Equal(0, agents.PoolCalls);                      // and so is the pool proposal
        Assert.Single((await store.GetCandidatesAsync("p1"))!.Substances);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    /// The known-candidate door is the ONE path into the record that no agent validates.
    /// DiscoveryAgent.Validate check-digits every CAS a model proposes — but ProvidedCandidates skips
    /// Discovery entirely and lands in the CandidatesDoc verbatim, so that rail never runs. From there the
    /// CAS flows into the regulatory screen, into dosing (against the wrong molecular weight) and into
    /// procurement, carrying exactly the authority of a candidate an agent had cited.
    ///
    /// A CAS check digit makes a transposed digit PROVABLY wrong, so there is no reason to let one through.
    /// The rest of Validate's rails are deliberately NOT applied here: these candidates come from the
    /// operator or an eval fixture, not from a model, so a hallucinated tier is not the risk. A mistyped
    /// CAS is.
    [Fact]
    public async Task ProvidedCandidateWithABadCheckDigit_IsRefused_NotWrittenAsCandidates()
    {
        var (d, store, agents, _) = Sut();
        ProvideCandidates(agents, "1314-23-5");                 // one digit off: the check digit is 4, not 5
        await Seed(store);
        await d.RunAsync("p1", default);

        Assert.Null(await store.GetCandidatesAsync("p1"));      // it must NOT become the candidate set
        var stage = (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery];
        Assert.Equal("needs-review", stage.Status);             // parked for the operator, the file's convention
        Assert.Contains("1314-23-5", stage.Error);              // and the record says which one and why
        Assert.Contains("check digit", stage.Error);
        Assert.Equal(0, agents.RegulatoryCalls);                // and nothing downstream ran on it
    }

    /// Non-numeric junk is the same defect wearing different clothes.
    [Fact]
    public async Task ProvidedCandidateWithAMalformedCas_IsRefused()
    {
        var (d, store, agents, _) = Sut();
        ProvideCandidates(agents, "cas-zr");
        await Seed(store);
        await d.RunAsync("p1", default);

        Assert.Null(await store.GetCandidatesAsync("p1"));
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Fact]
    public async Task DiscoveryNeedsReview_MarksStage_DoesNotCascade()
    {
        var (d, store, agents, _) = Sut();
        agents.Discovery = (_, _, _) => Task.FromResult(
            Smx.Backend.Agents.AgentRunResult<CandidatesDoc>.NeedsReview("no catalog hits"));
        await Seed(store);
        await d.RunAsync("p1", default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("needs-review", proj!.Stages[Stages.Discovery].Status);
        Assert.Null(await store.GetCandidatesAsync("p1"));
        Assert.Equal(0, agents.RegulatoryCalls);
    }

    // ---- regulatory + matrix --------------------------------------------------------------------------

    [Fact]
    public async Task CandidatesWritten_FansOutRegulatory_ThenAssemblesMatrix()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Single(await store.GetVerdictsAsync("p1"));
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("awaiting-RE", proj!.Stages[Stages.Regulatory].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
    }

    [Fact]
    public async Task RegulatoryFanOut_SkipsCellsThatAlreadyHaveVerdicts()
    {
        var (d, store, agents, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        var callsAfterFirst = agents.RegulatoryCalls;
        await d.RunAsync("p1", default);                        // a second pass
        Assert.Equal(callsAfterFirst, agents.RegulatoryCalls);
    }

    /// One parent run for the stage and one child per substance. The parent is what the operator cancels
    /// and what the UI groups under; children carry `subject` so each is nameable. Grouping is therefore
    /// EXPLICIT in the data rather than inferred from timing — fourteen substances screened in parallel
    /// produce fourteen interleaved run docs, and timing cannot tell you which stage they belong to.
    [Fact]
    public async Task Regulatory_opens_a_parent_run_and_one_child_per_substance()
    {
        var (d, store, agents, runs) = Sut();
        agents.Discovery = (_, c, _) => Task.FromResult(
            Smx.Backend.Agents.AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
            {
                Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
                Substances = [.. new[] { "cas-a", "cas-b", "cas-c" }.Select(cas =>
                    new CandidateSubstance("bottle", "Zr", "oxide", cas, null, null, true, "A", "ok",
                        [new Citation("catalog", "ref-catalog/x", "t")]))],
            }));
        await Seed(store);

        await d.RunAsync("p1", default);

        var regulatory = (await runs.ListAsync("p1", Stages.Regulatory, default)).ToList();
        var parent = Assert.Single(regulatory, r => r.ParentRunId is null);
        var children = regulatory.Where(r => r.ParentRunId == parent.Id).ToList();
        Assert.Equal(3, children.Count);
        Assert.All(children, c => Assert.NotNull(c.Subject));
        Assert.Null(parent.Subject);                                   // the parent IS the stage
        Assert.All(children, c => Assert.Equal(RunOutcome.Done, c.Outcome));
        // Every child names the substance it screened, so a fourteen-way fan-out reads as fourteen
        // nameable pieces of work rather than one opaque block.
        Assert.Equal(
            new[] { "cas-a|bottle", "cas-b|bottle", "cas-c|bottle" },
            children.Select(c => c.Subject).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// A substance the agent could not screen still gets a verdict — one that SAYS so. An absent verdict
    /// and a verdict reading "no cited verdict could be produced" are very different things downstream,
    /// and only the second one blocks the gate honestly. Its child run says needs-review to match.
    [Fact]
    public async Task AFailedChild_LeavesANeedsReviewVerdict_AndANeedsReviewChildRun()
    {
        var (d, store, agents, runs) = Sut();
        agents.Regulatory = (_, _, _) => Task.FromResult(
            Smx.Backend.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await Seed(store);

        await d.RunAsync("p1", default);

        var child = Assert.Single(
            await runs.ListAsync("p1", Stages.Regulatory, default), r => r.ParentRunId is not null);
        Assert.Equal(RunOutcome.NeedsReview, child.Outcome);
        Assert.Equal("no retrieval", child.Error);
        Assert.Equal(VerdictStatus.NeedsReview, Assert.Single(await store.GetVerdictsAsync("p1")).Overall);
    }

    /// A child that THROWS (not one that returns needs-review) must still be closed. Task.WhenAll carries
    /// the throw up to the parent, but nothing else would ever complete this child, and a run doc stuck at
    /// `running` is exactly the silent stall the trail exists to remove.
    [Fact]
    public async Task AThrowingChild_IsStillClosed_AndFailsTheParent()
    {
        var (d, store, agents, runs) = Sut();
        agents.Regulatory = (_, _, _) => throw new InvalidOperationException("foundry 503");
        await Seed(store);

        await d.RunAsync("p1", default);

        var regulatory = (await runs.ListAsync("p1", Stages.Regulatory, default)).ToList();
        Assert.DoesNotContain(regulatory, r => r.Outcome == RunOutcome.Running);
        var child = Assert.Single(regulatory, r => r.ParentRunId is not null);
        Assert.Equal(RunOutcome.Failed, child.Outcome);
        Assert.Contains("foundry 503", child.Error);
        var parent = Assert.Single(regulatory, r => r.ParentRunId is null);
        Assert.Equal(RunOutcome.Failed, parent.Outcome);
        Assert.Equal("failed", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    /// A stage's run ordinal counts PARENT runs. Counting the fan-out's children would make the second
    /// attempt at a three-substance screen `…|regulatory|5`, a number nobody can predict or reason about.
    [Fact]
    public async Task ASecondRegulatoryAttempt_TakesTheNextPARENTOrdinal()
    {
        var (d, store, agents, runs) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);

        // A new candidate with no verdict is what gives Regulatory work again.
        var candidates = (await store.GetCandidatesAsync("p1"))!;
        candidates.Substances.Add(new CandidateSubstance("bottle", "Y", "oxide", "cas-y", null, null, true,
            "A", "ok", [new Citation("catalog", "ref-catalog/x", "t")]));
        await store.UpsertCandidatesAsync(candidates);

        await d.RunAsync("p1", default);

        var parents = (await runs.ListAsync("p1", Stages.Regulatory, default))
            .Where(r => r.ParentRunId is null).Select(r => r.Id).ToList();
        Assert.Equal([RunIds.Run("p1", Stages.Regulatory, 1), RunIds.Run("p1", Stages.Regulatory, 2)], parents);
    }

    [Fact]
    public async Task RegulatoryNeedsReview_WritesPlaceholderVerdict_MatrixStillAssembles()
    {
        var (d, store, agents, _) = Sut();
        agents.Regulatory = (c, cand, _) => Task.FromResult(
            Smx.Backend.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await Seed(store);
        await d.RunAsync("p1", default);
        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.Single(verdicts);
        Assert.Equal(VerdictStatus.NeedsReview, verdicts[0].Overall);
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    // ---- the regulatory gate --------------------------------------------------------------------------

    [Fact]
    public async Task ApprovedRegulatoryGate_MovesRegulatoryStageToDone()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        await store.UpsertGateAsync(RegulatoryGateDoc("approved"));
        await d.OnGateAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task LockedRegulatoryGate_DoesNotAdvanceStage()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        await store.UpsertGateAsync(RegulatoryGateDoc("locked"));
        await d.OnGateAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    /// The gate signed BEFORE the verdicts were complete: the assembly is what reads the signature and
    /// promotes the stage, so a project whose analysis finishes under an already-approved gate lands
    /// `done` without a second signature.
    [Fact]
    public async Task GateApprovedBeforeVerdictsComplete_StageGoesDoneOnAssembly()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await store.UpsertGateAsync(RegulatoryGateDoc("approved"));
        await d.RunAsync("p1", default);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedNonRegulatoryGate_DoesNotAdvanceRegulatoryStage()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        // A VP gate flows through the same OnGateAsync — it must NOT advance Regulatory.
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", "vp"), ProjectId = "p1",
            GateType = "vp", Status = "approved", ApprovedAt = "t" });
        await d.OnGateAsync((await store.GetGateAsync("p1", "vp"))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedRegulatoryGate_DoesNotOverwriteFailedStage()
    {
        var (d, store, _, _) = Sut();
        var proj = await Seed(store);
        proj.Stages[Stages.Regulatory].Status = "failed";
        await store.UpsertProjectAsync(proj);

        await store.UpsertGateAsync(RegulatoryGateDoc("approved"));
        await d.OnGateAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("failed", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ApprovedRegulatoryGate_SignedTwice_StaysDone()
    {
        var (d, store, _, _) = Sut();
        await Seed(store);
        await d.RunAsync("p1", default);
        await store.UpsertGateAsync(RegulatoryGateDoc("approved"));
        var gate = (await store.GetGateAsync("p1", GateTypes.Regulatory))!;
        await d.OnGateAsync(gate, default);
        await d.OnGateAsync(gate, default); // a second signature must be a no-op
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }
}
