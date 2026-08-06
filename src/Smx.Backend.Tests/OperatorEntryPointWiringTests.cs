using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Backend.Agents;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// THE ENDPOINT MUST REACH THE HANDLER. Every test here drives an OPERATOR DOOR over real HTTP and asserts
/// the consequence the operator is entitled to expect.
///
/// They exist because of a whole class of regression that a green suite could not see. Under the change
/// feed, writing a record WAS the dispatch: the determination endpoint wrote a VP gate and something else
/// closed the project, the revise endpoint wrote a RevisionDoc and something else applied it, the chat door
/// wrote a message and something else answered it. Folding the orchestrator into the backend deleted the
/// feed and ported the handlers into PipelineRunner — where nothing called four of them. Every existing test
/// of those features invokes the runner method DIRECTLY, so all of them stayed green for the entire outage
/// while the features were, from an operator's chair, gone.
///
/// So: no test in this file may call a PipelineRunner method. The door is the subject.
public class OperatorEntryPointWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "p1";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryRunStore _runs = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly FakeAgentRuns _agents = new();
    private readonly PipelineRunner _runner;
    private readonly PipelineSupervisor _supervisor;

    public OperatorEntryPointWiringTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _runner = new PipelineRunner(_store, _runs, _agents, new ThreadEventHub(),
            new LearnedConclusionWriter(_knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
                NullLogger<LearnedConclusionWriter>.Instance),
            2, knowledge: _knowledge);
        _supervisor = new PipelineSupervisor(_store, _runs, _runner, NullLogger<PipelineSupervisor>.Instance);
    }

    /// The crux, verbatim from the Plan-4/5 E2E harnesses: the SAME stores stand behind the HTTP app and the
    /// runner the supervisor drives, so an HTTP write is exactly what the runner then reads.
    private HttpClient Client() => _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.AddSingleton<IRecordStore>(_store);
        s.AddSingleton<IRunStore>(_runs);
        s.AddSingleton<IKnowledgeStore>(_knowledge);
        s.AddSingleton(new ThreadEventHub());
        s.AddSingleton(_runner);
        s.AddSingleton(_supervisor);
    })).CreateClient();

    private async Task<StageState> StageAsync(string stage) => (await _store.GetProjectAsync(P))!.Stages[stage];

    private static Citation Cited => new("regulatory", "x", "t");

    private async Task<ProjectDoc> SeedProjectAsync(params (string Stage, string Status)[] stages)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var (stage, status) in stages) project.Stages[stage].Status = status;
        await _store.UpsertProjectAsync(project);
        return project;
    }

    /// One component, one compliant substance, and the physicist's numbers on file — the smallest project
    /// that can reach Dosing.
    private async Task SeedComplianceAsync(bool withBackground = true)
    {
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
            MeasuredBackgrounds = withBackground ? [new("bottle", "Zr", 5.0, "ppm")] : [],
            Device = withBackground ? new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]) : null,
        });
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, false, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-zr", "bottle"), ProjectId = P, Cas = "cas-zr",
            ComponentId = "bottle", Element = "Zr", Form = "neodecanoate", EvidenceReviewed = true,
            Determination = Determinations.Recommended, DeterminationReason = "cleared",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [Cited], 0.9, "ok")],
        });
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-27T09:00:00.0000000+00:00",
        });
    }

    // -------------------------------------------------------------------------------------------------
    // 1. POST /projects/{id}/decision/determination — the VP signature CLOSES the project.
    // -------------------------------------------------------------------------------------------------

    /// The end of the entire journey. The VP's approval is what "releases procurement + writes to Marker
    /// Library + Learned Conclusions" (spec §4), and for the whole time nothing called CloseProjectAsync the
    /// endpoint wrote an approved gate and NONE of that happened: procurement stayed unreleased, the library
    /// never learned the code, and Decision sat at `awaiting-VP` under a signature that already existed —
    /// the dashboard blaming a VP who had already signed.
    ///
    /// Asserted on the CONSEQUENCES, never on the gate document: a gate row is what the endpoint wrote all
    /// along, and it proved nothing.
    [Fact]
    public async Task VpDetermination_ClosesTheProject_ReleasesProcurement_AndWritesTheMarkerLibrary()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"), (Stages.Dosing, "done"), (Stages.Cost, "done"),
            (Stages.Decision, StageStatus.AwaitingVp));
        await SeedComplianceAsync();

        // Two markers, because a code IS the ratio between them — RatioSignature refuses to form one from a
        // single marker, and the signature is the identity the whole close is keyed on.
        var code = new MarkerCode("bottle",
            [new CodeMarker("cas-zr", "Zr", 40.0, 0.74, ElementMassMg: 1.0, CompoundMassMg: 2.0),
             new CodeMarker("cas-y", "Y", 20.0, 0.70, ElementMassMg: 0.5, CompoundMassMg: 1.0)],
            "the compliant pair");
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, Codes = [code],
            GeneratedAt = "2026-07-27T10:00:00Z",
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P,
            Components = [new("bottle", [], new ProposedCode(code.RatioSignature, ["cas-zr", "cas-y"], "proposed"))],
            GeneratedAt = "2026-07-27T10:00:00Z",
        });

        var res = await Client().PostAsJsonAsync($"/projects/{P}/decision/determination", new
        {
            determination = "approved",
            reason = "codes reviewed against the decision matrix on 27 Jul",
            confirmations = new[] { new { componentId = "bottle", code = code.RatioSignature } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Procurement released — without it POST /orders/{cas} refuses every order forever.
        var decision = (await _store.GetDecisionAsync(P))!;
        Assert.Equal(ProcurementStatus.Released, decision.Procurement.Status);
        // The Marker Library learned the approved code: this is the cross-project payoff of the whole run.
        var marker = Assert.Single(await _knowledge.QueryMarkersAsync(null));
        Assert.Equal(P, marker.SourceProject);
        Assert.Equal(code.RatioSignature, marker.Composition.Ratio);
        // ...and a Learned Conclusion recording the close.
        var conclusion = await _knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Decision, $"{P}|close");
        Assert.Contains(code.RatioSignature, conclusion!.Finding);
        // The stage left the park. `awaiting-VP` under an approved gate is the exact stall this fixes.
        Assert.Equal("done", (await StageAsync(Stages.Decision)).Status);
    }

    /// The at-least-once bookkeeping in CloseProjectAsync was written for a change feed; called exactly once
    /// from an endpoint it still has to hold, because a double-POST is a thing an operator does. The latch
    /// on `awaiting-VP` → `done` is what makes the second one a no-op: no second library entry, no
    /// re-counted reuse, no re-embedded conclusion.
    [Fact]
    public async Task VpDetermination_TwiceOver_ClosesOnce()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"), (Stages.Dosing, "done"), (Stages.Cost, "done"),
            (Stages.Decision, StageStatus.AwaitingVp));
        await SeedComplianceAsync();
        var code = new MarkerCode("bottle",
            [new CodeMarker("cas-zr", "Zr", 40.0, 0.74, 1.0, 2.0),
             new CodeMarker("cas-y", "Y", 20.0, 0.70, 0.5, 1.0)], "the compliant pair");
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, Codes = [code], GeneratedAt = "2026-07-27T10:00:00Z",
        });
        await _store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P,
            Components = [new("bottle", [], new ProposedCode(code.RatioSignature, ["cas-zr", "cas-y"], "proposed"))],
            GeneratedAt = "2026-07-27T10:00:00Z",
        });
        var client = Client();
        var body = new
        {
            determination = "approved", reason = "reviewed",
            confirmations = new[] { new { componentId = "bottle", code = code.RatioSignature } },
        };

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/projects/{P}/decision/determination", body)).StatusCode);
        // The second signature is refused by the park guard — the stage is `done`, not `awaiting-VP` — which
        // is the outer half of the same protection. Either way, nothing is written twice.
        var second = await client.PostAsJsonAsync($"/projects/{P}/decision/determination", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        Assert.Single(await _knowledge.QueryMarkersAsync(null));
        Assert.Equal(0, Assert.Single(await _knowledge.QueryMarkersAsync(null)).ReuseCount);
    }

    // -------------------------------------------------------------------------------------------------
    // 2. POST /projects/{id}/stages/{stage}/revise — revise-with-reason actually revises.
    // -------------------------------------------------------------------------------------------------

    /// Law 4, through the door. "Change this, and here is why" was being recorded as a RevisionDoc and then
    /// ignored: the stage was never re-run, the operator's reason was never distilled into a Learned
    /// Conclusion, and the gate the revision invalidates kept standing. The revision even stayed `pending`
    /// forever, which silently holds the VP gate shut (VpGate.PendingRevisionBlocker).
    [Fact]
    public async Task Revise_ReRunsTheStage_RecordsTheConclusion_AndVoidsTheSignature()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"));
        await SeedComplianceAsync();

        RevisionDoc? seenByAgent = null;
        _agents.Discovery = (_, c, revision) =>
        {
            seenByAgent = revision;
            return Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
            {
                Id = RecordIds.Candidates(P), ProjectId = P,
                Substances = [new("bottle", "Zr", "octoate", "cas-zr-oct", null, null, false, "A", "revised",
                    [new Citation("catalog", "ref-catalog/x", "t")])],
            }));
        };

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/discovery/revise", new
        {
            target = "Zr neodecanoate on bottle",
            reason = "prefer the octoate — the neodecanoate bleeds in HDPE",
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion(P);   // the work is async by design; the 202 says so

        // The agent was re-run WITH the operator's directive — not merely re-run.
        Assert.Equal("prefer the octoate — the neodecanoate bleeds in HDPE", seenByAgent!.Reason);
        // The analysis was actually replaced.
        Assert.Equal("octoate", Assert.Single((await _store.GetCandidatesAsync(P))!.Substances).Form);
        // The revision landed — a permanently `pending` one is a permanent hold on the VP gate.
        var applied = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        // The system got smarter: the reason reached the knowledge layer VERBATIM.
        var conclusion = Assert.Single(await _knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Contains("the neodecanoate bleeds in HDPE", string.Join(" ", conclusion.Provenance.Decisions));
        // And the signature the revision invalidated no longer stands over analysis nobody reviewed.
        Assert.Equal("locked", (await _store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
    }

    // -------------------------------------------------------------------------------------------------
    // 3. POST /projects/{id}/stages/{stage}/chat — the message is ANSWERED.
    // -------------------------------------------------------------------------------------------------

    /// The operator asked a question and the system filed it. This is the door the frontend actually posts to
    /// (`api/client.ts` → `/stages/{stage}/chat`), and between the change feed's removal and its rewiring
    /// every turn went into the transcript unanswered.
    [Fact]
    public async Task Chat_RunsTheTurn_AndTheReplyLandsOnTheThread()
    {
        await SeedProjectAsync((Stages.Intake, "done"), (Stages.Discovery, "done"));
        await SeedComplianceAsync();

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/discovery/chat",
            new { text = "why did you drop the Zr neodecanoate?" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.DetachedCompletion();   // the turn runs beside the pipeline, not inside it

        Assert.Equal(1, _agents.ChatCalls);
        var thread = await _store.GetChatThreadAsync(P, Stages.Discovery);
        Assert.Equal(2, thread.Count);
        Assert.Equal(ChatRoles.Agent, thread[1].Role);
        Assert.Equal("Echo: why did you drop the Zr neodecanoate?", thread[1].Text);
        // The message is no longer waiting on anything.
        Assert.Equal(ChatStatus.Answered, thread[0].Status);
    }

    /// A chat turn must be answerable WHILE the pipeline runs — the operator argues with the agent about the
    /// stage in front of them, and Regulatory can take minutes. It is why the turn is detached rather than
    /// taking the project's exclusive slot.
    [Fact]
    public async Task Chat_IsAnswered_EvenWhileAPipelineHoldsTheProject()
    {
        await SeedProjectAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents.Intake = async _ =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, _agents.LastIntakeToken);
            return AgentRunResult<ConstraintsDoc>.NeedsReview("unreachable");
        };
        Assert.True(_supervisor.TryStart(P));
        await reached.Task;

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/intake/chat",
            new { text = "what are you doing?" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.DetachedCompletion();
        Assert.Equal("Echo: what are you doing?",
            (await _store.GetChatThreadAsync(P, Stages.Intake))[1].Text);

        Assert.True(_supervisor.CancelRun(RunIds.Run(P, Stages.Intake, 1)));
        await _supervisor.Completion(P);
    }

    // -------------------------------------------------------------------------------------------------
    // 3b. …and the WORK a chat turn leaves behind is drained.
    //
    // THE INVARIANT: a pending revision is always eventually applied or explicitly failed. A RevisionDoc is
    // durable the moment `apply_revision` writes it, and while it is `pending` it holds the VP gate shut
    // (VpGate.PendingRevisionBlocker) — so an agent that says "queued, the stage will re-run" over a
    // revision nothing applies is not merely unhelpful, it is a lie that also blocks the journey's last gate.
    // -------------------------------------------------------------------------------------------------

    /// Makes the scripted chat turn call `apply_revision` exactly as the real agent would — the tools
    /// instance is the one the runner built, bound to this project and stage.
    private void ChatQueuesARevision(string target, string reason) =>
        _agents.Chat = async (tools, _, _, message) =>
        {
            await tools.ApplyRevisionAsync(target, reason);
            return $"Queued: {message}";
        };

    /// The idle case. Nothing else is running, so nothing else is coming: the turn claims the project's slot
    /// on its way out and the drain applies what it queued.
    [Fact]
    public async Task ChatQueuedRevision_IsApplied_WhenNothingElseHoldsTheProject()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"));
        await SeedComplianceAsync();
        _agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "octoate", "cas-zr", null, null, false, "A", "revised",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        }));
        ChatQueuesARevision("Zr neodecanoate on bottle", "it bleeds in HDPE — use the octoate");

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/discovery/chat",
            new { text = "swap the neodecanoate, it bleeds" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.DetachedCompletion();

        var revision = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, revision.Status);
        Assert.Equal("octoate", Assert.Single((await _store.GetCandidatesAsync(P))!.Substances).Form);
        // And the signature over the analysis it replaced is void — the chat door must not be a way around
        // the gate the revise door voids.
        Assert.Equal("locked", (await _store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
    }

    /// The busy case, which is the one that used to strand a revision permanently. The turn cannot take the
    /// slot, so it steps aside; the pipeline that holds the slot drains on its way out. Neither path knows
    /// about the other — they converge on "nothing is pending".
    [Fact]
    public async Task ChatQueuedRevision_IsApplied_ByTheSlotHolder_WhenAPipelineIsMidRun()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"));
        await SeedComplianceAsync();
        await _knowledge.UpsertSubstancePropertyAsync(new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("cas-zr"), Cas = "cas-zr", Element = "Zr",
            Form = "neodecanoate", MetalLoading = 0.74, Basis = "assay", EnteredAt = "2026-07-27T09:00:00Z",
        });

        // Park a live pipeline inside Dosing — a genuinely held slot, on a project that already has the
        // candidates a Discovery revision needs.
        var inDosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letDosingFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents.Dosing = async (c, _, _, _, _) =>
        {
            inDosing.TrySetResult();
            await letDosingFinish.Task;
            return AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId, GeneratedAt = "2026-07-27T11:00:00Z",
            });
        };
        _agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "octoate", "cas-zr", null, null, false, "A", "revised",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        }));
        ChatQueuesARevision("Zr neodecanoate on bottle", "it bleeds in HDPE — use the octoate");

        Assert.True(_supervisor.TryStart(P));
        await inDosing.Task;

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/discovery/chat",
            new { text = "swap the neodecanoate, it bleeds" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.DetachedCompletion();

        // The turn is answered and the revision is durable — but nothing has applied it, because the slot
        // was never free. This is exactly the state that used to be permanent.
        Assert.Equal(RevisionStatus.Pending, Assert.Single(await _store.GetRevisionsAsync(P)).Status);
        Assert.True(_supervisor.IsRunning(P));

        letDosingFinish.SetResult();
        await _supervisor.Completion(P);

        Assert.Equal(RevisionStatus.Applied, Assert.Single(await _store.GetRevisionsAsync(P)).Status);
        Assert.Equal("octoate", Assert.Single((await _store.GetCandidatesAsync(P))!.Substances).Form);
    }

    /// The hazard the loop creates: applying a revision can PRODUCE a revision, and a self-feeding chain
    /// would spin here forever — burning model calls, holding the slot, telling nobody. The bound turns that
    /// into a prompt, readable failure. That this test TERMINATES is half of what it asserts.
    [Fact]
    public async Task DrainStopsAtItsBound_AndFailsTheRemainderReadably()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"));
        await SeedComplianceAsync();

        // Each application queues its successor — the pathological case, scripted.
        var queued = 0;
        _agents.Discovery = (_, _, _) =>
        {
            var next = ++queued;
            _store.UpsertRevisionAsync(new RevisionDoc
            {
                Id = RecordIds.Revision(P, Stages.Discovery, $"spawn{next}"), ProjectId = P,
                Stage = Stages.Discovery, Target = "again", Reason = $"and again ({next})",
                Status = RevisionStatus.Pending, CreatedAt = $"2026-07-27T12:00:{next:00}.0000000+00:00",
            }).GetAwaiter().GetResult();
            return Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
            {
                Id = RecordIds.Candidates(P), ProjectId = P,
                Substances = [new("bottle", "Zr", "octoate", "cas-zr", null, null, false, "A", "revised",
                    [new Citation("catalog", "ref-catalog/x", "t")])],
            }));
        };

        var res = await Client().PostAsJsonAsync($"/projects/{P}/stages/discovery/revise", new
        {
            target = "Zr neodecanoate on bottle", reason = "start the chain",
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion(P);

        var revisions = await _store.GetRevisionsAsync(P);
        // Nothing is left pending: the invariant holds even in the pathological case.
        Assert.DoesNotContain(revisions, r => r.Status == RevisionStatus.Pending);
        var stopped = Assert.Single(revisions, r => r.Status == RevisionStatus.Failed);
        Assert.Contains("drain passes", stopped.Error);
        // ...and the chain was bounded rather than merely slow.
        Assert.InRange(revisions.Count, 2, 8);
    }

    // -------------------------------------------------------------------------------------------------
    // 4. POST /projects/{id}/dosing/loading — the answer to an `awaiting-operator` park RESUMES it.
    // -------------------------------------------------------------------------------------------------

    /// The async pause/resume loop, which is the product's core interaction: an agent parks on something only
    /// a human can supply, the operator supplies it days later, the agent resumes. Dosing parks on a missing
    /// metal loading; this endpoint is the only answer to that park. Its own comment used to say "production's
    /// change feed re-runs Dosing off this write" — there is no change feed, so the loading was accepted, the
    /// stage was flipped to `pending`, and the project sat there looking about to run.
    [Fact]
    public async Task Loading_ReopensDosing_SoTheDroppedSubstanceGetsDosed()
    {
        await SeedProjectAsync(
            (Stages.Intake, "done"), (Stages.Discovery, "done"), (Stages.Regulatory, "done"),
            (Stages.Matrix, "done"));
        await SeedComplianceAsync();

        // Was an awaiting-operator PARK. The pipeline no longer parks (execution-core §8): a substance whose
        // metal loading is unknown is DROPPED and named instead — and here it was the only dosable one, so
        // the stage lands needs-review with nothing dosed. This endpoint is still the answer to that, and
        // re-opening Dosing to `pending` is still what makes the second pass happen.
        Assert.True(_supervisor.TryStart(P));
        await _supervisor.Completion(P);
        var stopped = await StageAsync(Stages.Dosing);
        Assert.Equal(StageStatus.NeedsReview, stopped.Status);
        Assert.Contains("metal loading", stopped.Error);

        var res = await Client().PostAsJsonAsync($"/projects/{P}/dosing/loading", new
        {
            cas = "cas-zr", element = "Zr", form = "neodecanoate",
            metalLoading = 0.74, basis = "supplier assay, 27 Jul",
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion(P);
        // Dosing RAN: the agent was called and the stage left the park.
        Assert.Equal(1, _agents.DosingCalls);
        Assert.Equal("done", (await StageAsync(Stages.Dosing)).Status);
    }

    // -------------------------------------------------------------------------------------------------
    // 5. POST /projects/{id}/xrf/confirm — the physicist's measurement UN-PARKS Discovery.
    // -------------------------------------------------------------------------------------------------

    /// The other half of the pause/resume loop, and the same dead wire: `confirm`'s own comment said "this
    /// write IS the dispatch". It was, once. Afterwards the operator entered the physicist's measured
    /// background, got a 202, and nothing moved.
    [Fact]
    public async Task XrfConfirm_ResumesDiscoveryWithTheMeasuredPool()
    {
        await SeedProjectAsync((Stages.Intake, "done"));
        // Intake done, but no element pool on file and no agent-proposed pool: Discovery has nothing to
        // screen against and the project is parked on the physicist.
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        });
        // Echoes the pool it is handed, so the assertion below can prove the PHYSICIST'S element reached
        // Discovery rather than merely that some Discovery run happened.
        _agents.Discovery = (_, c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [.. c.ElementPools.Select(e => new CandidateSubstance(
                e.Component, e.Element, "neodecanoate", $"cas-{e.Element}", null, null, false, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")]))],
        }));

        var res = await Client().PostAsJsonAsync($"/projects/{P}/xrf/confirm", new
        {
            proposals = new[]
            {
                new
                {
                    rowNumber = 2, component = "bottle", element = "Ba", line = "Ka", status = "V",
                    backgroundLevel = 12.5, backgroundUnit = "ppm",
                    deviceModel = "Niton XL5", deviceLod = 3.0, deviceLodUnit = "ppm",
                    problems = Array.Empty<string>(),
                },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion(P);
        var candidates = await _store.GetCandidatesAsync(P);
        Assert.Equal("Ba", Assert.Single(candidates!.Substances).Element);
    }
}
