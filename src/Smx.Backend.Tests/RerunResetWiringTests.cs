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

/// THE STAGE RESETS THAT WERE NO-OPS, driven through the runner.
///
/// <c>RerunDiffWiringTests</c> pins the same property for Regulatory. It was not the only stage with the
/// defect — it was the only one that had been fixed. `material` and `objective` declare `Everything` as
/// their blast radius, but RunPoolAsync and RunDiscoveryAsync both skipped on DOC EXISTENCE, so POST
/// /amendments reset them to `pending`, the runner walked them, each found its own output already on file
/// and skipped. The endpoint reported `rerun: [pool, discovery, regulatory, …]` over an analysis in which
/// nothing but Regulatory re-ran. A rerun that is reported and never happens is worse than no rerun: the
/// operator believes the record now describes the requirement they just changed.
///
/// Also here: what a Discovery re-run does to the verdicts it ORPHANS, and the DosingDoc a run that can
/// dose nothing must not leave standing.
public class RerunResetWiringTests
{
    private const string P = "p1";

    private static (PipelineRunner Runner, InMemoryRecordStore Store, FakeAgentRuns Agents,
                    InMemoryRunStore Runs, InMemoryKnowledgeStore Knowledge) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var runs = new InMemoryRunStore();
        var knowledge = new InMemoryKnowledgeStore();
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, runs, agents, new ThreadEventHub(), conclusions, 2,
                    knowledge: knowledge),
                store, agents, runs, knowledge);
    }

    private static async Task<List<string>> StepsAsync(InMemoryRunStore runs, string stage) =>
        [.. (await runs.ListAsync(P, stage)).SelectMany(r => r.Steps).Select(s => s.Text)];

    // ---- fixtures --------------------------------------------------------------------------------------

    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
        MeasuredBackgrounds = [new("bottle", "Zr", 5.0, "ppm")],
        Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]),
    };

    private static CandidateSubstance Candidate(string cas, string element) =>
        new("bottle", element, "neodecanoate", cas, null, null, false, "A", "ok",
            [new Citation("catalog", "x", "t")]);

    private static CandidatesDoc CandidatesOf(params CandidateSubstance[] substances) => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P, Substances = [.. substances],
    };

    private static VerdictDoc Verdict(string cas, string element, string? determination = null) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
        Cas = cas, ComponentId = "bottle", Element = element, Form = "neodecanoate",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
    };

    private static PoolDoc Pool() => new()
    {
        Id = RecordIds.Pool(P), ProjectId = P,
        Suggestions = [new("bottle", "Zr", "compound", "an oxide suits a solid polymer", [])],
    };

    /// A project in the exact state POST /amendments leaves it for a `material` change: every stage in the
    /// `Everything` blast radius back to `pending` with its attempt already counted, and every stage's prior
    /// output still on file.
    private static async Task<ProjectDoc> SeedAmendedAsync(
        InMemoryRecordStore store, string resetStatus = StageStatus.Pending)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        foreach (var stage in new[] { Stages.Pool, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
        {
            project.Stages[stage].Status = resetStatus;
            project.Stages[stage].Attempts = 1;
        }
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertPoolAsync(Pool());
        await store.UpsertCandidatesAsync(CandidatesOf(Candidate("cas-zr", "Zr")));
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", Determinations.Recommended));
        return project;
    }

    // ---- Pool ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAmendmentReset_ReProposesThePool_RatherThanSkippingOverTheOneOnFile()
    {
        // The bug. `material` declares Everything — the polymer decides which chemistry is compatible at all
        // — but RunPoolAsync's guard was `GetPoolAsync(...) is not null`, so the reset was a no-op and the
        // project kept a pool proposed for a polymer the customer no longer uses.
        var (runner, store, agents, _, _) = Sut();
        await SeedAmendedAsync(store);
        agents.Pool = (_, c, _) => Task.FromResult(AgentRunResult<PoolDoc>.Ok(new PoolDoc
        {
            Id = RecordIds.Pool(P), ProjectId = c.ProjectId,
            Suggestions = [new("bottle", "Y", "compound", "re-proposed for the new polymer", [])],
        }));

        await runner.RunAsync(P, default);

        Assert.Equal(1, agents.PoolCalls);
        Assert.Equal("Y", Assert.Single((await store.GetPoolAsync(P))!.Suggestions).Element);
    }

    [Theory]
    [InlineData(StageStatus.Done)]
    [InlineData(StageStatus.Running)]
    [InlineData(StageStatus.Failed)]
    [InlineData(StageStatus.NeedsReview)]
    public async Task OnlyPending_ReProposesThePool_SoAnIdlePassNeverReRunsAFinishedStage(string status)
    {
        // `pending` WITH a PoolDoc on file is the signature of an amendment reset and of nothing else. Any
        // other status re-proposing would mean every idle pipeline pass re-ran the pool agent forever.
        var (runner, store, agents, _, _) = Sut();
        await SeedAmendedAsync(store, resetStatus: status);

        await runner.RunAsync(P, default);

        Assert.Equal(0, agents.PoolCalls);
    }

    [Fact]
    public async Task AProjectThatNeverNeededAPool_StaysPending_WithoutEverRunningTheAgent()
    {
        // The near-miss the re-open condition has to survive. A pool stage that legitimately never runs —
        // the operator supplied an element pool — sits at `pending` forever with NO PoolDoc. If the re-open
        // asked only "is this stage pending", this project would run the pool agent on every single pass.
        var (runner, store, agents, _, _) = Sut();
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        await store.UpsertProjectAsync(project);
        var c = Constraints();
        c.ElementPools = [new("bottle", "Zr", "Kα", "V")];
        await store.UpsertConstraintsAsync(c);

        await runner.RunAsync(P, default);

        Assert.Equal(0, agents.PoolCalls);
        Assert.Equal(StageStatus.Pending, (await store.GetProjectAsync(P))!.Stages[Stages.Pool].Status);
    }

    // ---- Discovery -------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAmendmentReset_ReDiscovers_RatherThanSkippingOverTheCandidatesOnFile()
    {
        var (runner, store, agents, _, _) = Sut();
        await SeedAmendedAsync(store);
        agents.Discovery = (_, c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(
            CandidatesOf(Candidate("cas-y", "Y"))));

        await runner.RunAsync(P, default);

        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.Equal("cas-y", Assert.Single((await store.GetCandidatesAsync(P))!.Substances).Cas);
    }

    [Theory]
    [InlineData(StageStatus.Done)]
    [InlineData(StageStatus.Running)]
    [InlineData(StageStatus.Failed)]
    [InlineData(StageStatus.NeedsReview)]
    public async Task OnlyPending_ReDiscovers_SoAnIdlePassNeverReRunsAFinishedStage(string status)
    {
        var (runner, store, agents, _, _) = Sut();
        await SeedAmendedAsync(store, resetStatus: status);

        await runner.RunAsync(P, default);

        Assert.Equal(0, agents.DiscoveryCalls);
    }

    [Fact]
    public async Task ADiscoveryRerun_DeletesTheVerdictsItOrphans()
    {
        // THE DECISION (see PipelineRunner.PruneOrphanedVerdictsAsync). A re-run that drops a candidate
        // leaves a verdict describing a cell nobody screens. Four readers filter those out and two do not:
        // GET /verdicts serves the partition raw, and RunDosingAsync folds CompliantSet over ALL verdicts
        // - so an orphan carrying `recommended` is dosed into a code for a substance the current analysis
        // rejected. The document is removed rather than filtered.
        var (runner, store, agents, _, _) = Sut();
        await SeedAmendedAsync(store);
        await store.UpsertCandidatesAsync(CandidatesOf(Candidate("cas-zr", "Zr"), Candidate("cas-y", "Y")));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y", Determinations.Recommended));
        // The new polymer suits Zr only.
        agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(
            CandidatesOf(Candidate("cas-zr", "Zr"))));

        await runner.RunAsync(P, default);

        var verdicts = await store.GetVerdictsAsync(P);
        Assert.DoesNotContain(verdicts, v => v.Cas == "cas-y");
        Assert.Contains(verdicts, v => v.Cas == "cas-zr");
    }

    [Fact]
    public async Task ADiscoveryRerun_NamesTheOrphanedVerdictOnTheTrail_RatherThanDroppingItSilently()
    {
        // A substance leaving the analysis is the loudest thing RerunDiff can report, and Discovery is the
        // one stage where it happens. Deleting quietly would trade one invisible failure for another.
        var (runner, store, agents, runs, _) = Sut();
        await SeedAmendedAsync(store);
        await store.UpsertCandidatesAsync(CandidatesOf(Candidate("cas-zr", "Zr"), Candidate("cas-y", "Y")));
        await store.UpsertVerdictAsync(Verdict("cas-y", "Y", Determinations.Recommended));
        agents.Discovery = (_, _, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(
            CandidatesOf(Candidate("cas-zr", "Zr"))));

        await runner.RunAsync(P, default);

        var steps = await StepsAsync(runs, Stages.Discovery);
        Assert.Contains(steps, s => s.Contains("Y (cas-y) in 'bottle': NO LONGER SCREENED"));
    }

    [Fact]
    public async Task AFirstDiscovery_EmitsNoOrphanReportAtAll()
    {
        // A first run has nothing to orphan. "No prior state to compare" on every project's first Discovery
        // would make the sentence ordinary, and its whole value is that it is unusual enough to read.
        var (runner, store, _, runs, _) = Sut();
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertPoolAsync(Pool());

        await runner.RunAsync(P, default);

        var steps = await StepsAsync(runs, Stages.Discovery);
        Assert.NotEmpty(steps);                                     // the stage really did run
        Assert.DoesNotContain(steps, s => s.Contains("prior state"));
        Assert.DoesNotContain(steps, s => s.Contains("Reran"));
    }

    // ---- Dosing: a run that can dose nothing --------------------------------------------------------

    /// A dosing on file from an earlier run, with one window and one two-marker code — a code is 2–3 markers
    /// by definition, so a one-marker fixture would not survive the store's round-trip.
    private static DosingDoc PriorDosing() => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "2026-08-06T00:00:00Z",
        Windows =
        [
            new("bottle", "cas-zr", "Zr",
                new Bound(6, "measured background", BoundKinds.Measured, 1),
                new Bound(500, "no cap found", BoundKinds.Estimate, 0.5), 40, 12),
        ],
        Codes =
        [
            new("bottle",
                [
                    new CodeMarker("cas-zr", "Zr", 40, 0.74, 29.6, 200),
                    new CodeMarker("cas-y", "Y", 20, 0.78, 78, 100),
                ], "why"),
        ],
    };

    /// Dosing re-opened over a compliant set that has since become empty: neither a ruling nor a proposal of
    /// `recommended` survives, so CompliantSet folds to nothing and the run cannot dose a single substance.
    private static async Task SeedDosingWithNothingDosableAsync(InMemoryRecordStore store)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var stage in new[]
                 { Stages.Intake, Stages.Pool, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
            project.Stages[stage].Status = StageStatus.Done;
        project.Stages[Stages.Dosing].Status = StageStatus.Pending;
        project.Stages[Stages.Dosing].Attempts = 1;
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(CandidatesOf(Candidate("cas-zr", "Zr")));
        // Rejected: nothing carries a determination OR a proposal of `recommended`.
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", Determinations.Rejected));
        await store.UpsertDosingAsync(PriorDosing());
    }

    [Fact]
    public async Task ADosingRunThatCanDoseNothing_ReplacesTheDosingOnFile_RatherThanLeavingItStanding()
    {
        // The bug. The zero-substance exits returned needs-review and wrote NOTHING, so the previous run's
        // ppm windows, codes and ORDER AMOUNTS stayed on file describing inputs that no longer hold — under
        // a stage the operator reads as needing review. Same "wrong but looks current" family the Matrix
        // invalidation rule exists for, and worse than a stale matrix because procurement acts on these.
        var (runner, store, _, _, _) = Sut();
        await SeedDosingWithNothingDosableAsync(store);

        await runner.RunAsync(P, default);

        var dosing = (await store.GetDosingAsync(P))!;
        Assert.Empty(dosing.Windows);
        Assert.Empty(dosing.Codes);
        Assert.True(dosing.Provisional);
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("nothing may be dosed"));
        Assert.Equal(StageStatus.NeedsReview, (await store.GetProjectAsync(P))!.Stages[Stages.Dosing].Status);
    }

    [Fact]
    public async Task ADosingRunThatCanDoseNothing_WritesADocument_RatherThanDeletingTheOldOne()
    {
        // Absence would read as "Dosing has not run" everywhere downstream — RunDecisionAsync skips on a
        // null DosingDoc, ProjectTable renders no dosing cells. An empty document says both halves: it ran,
        // and it could dose nothing, and here is why.
        var (runner, store, _, _, _) = Sut();
        await SeedDosingWithNothingDosableAsync(store);

        await runner.RunAsync(P, default);

        Assert.NotNull(await store.GetDosingAsync(P));
    }

    [Fact]
    public async Task ADosingRunThatCanDoseNothing_NamesEverySubstanceItStoppedDosing()
    {
        // The §9.5 diff is owed here MOST of all: every substance the previous run dosed has just left the
        // record, and a silent replacement is exactly the "rerun that changed something and said nothing"
        // this mechanism exists to prevent.
        var (runner, store, _, runs, _) = Sut();
        await SeedDosingWithNothingDosableAsync(store);

        await runner.RunAsync(P, default);

        var steps = await StepsAsync(runs, Stages.Dosing);
        Assert.Contains(steps, s => s.Contains("Zr (cas-zr) in 'bottle': NO LONGER DOSED"));
    }

    [Fact]
    public async Task ADosingRunWhoseInputsAreAllMissing_AlsoReplacesTheDosingOnFile()
    {
        // The second zero-substance exit: the set is non-empty but every substance is DROPPED for a missing
        // input (here, no metal loading is on file — an absent loading is not 1.0, which would under-order
        // an oxide). Same stale document, same fix; two exits, so two tests.
        var (runner, store, _, _, _) = Sut();
        await SeedDosingWithNothingDosableAsync(store);
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", Determinations.Recommended));

        await runner.RunAsync(P, default);

        var dosing = (await store.GetDosingAsync(P))!;
        Assert.Empty(dosing.Codes);
        Assert.True(dosing.Provisional);
        Assert.Contains(dosing.ProvisionalReasons, r => r.Contains("metal loading"));
    }
}
