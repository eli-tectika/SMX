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

/// The §9.5 rerun diff, driven through the runner rather than asserted on the pure function
/// (<c>RerunDiffTests</c> owns that). Two properties live here and only here:
///
/// 1. AN AMENDMENT ACTUALLY RE-SCREENS. Before this work the whole rerun mechanism was decorative on
///    Regulatory: POST /amendments reset the stage to `pending`, the runner arrived, found a verdict on file
///    for every candidate, and skipped — so the record went on displaying an analysis screened against markets
///    the customer no longer sells into, with no error anywhere. A diff over a rerun that never happens is
///    worse than no diff, because "nothing changed" is then a lie the machine tells confidently.
/// 2. THE DIFF REACHES THE OPERATOR. The lines are written onto the run's trail, which is the surface that
///    exists only if the stage actually ran.
public class RerunDiffWiringTests
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

    /// Every step text the stage's runs wrote, parents and children together — the operator reads one timeline.
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

    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, false, "A", "ok",
                [new Citation("catalog", "x", "t")]),
        ],
    };

    private static VerdictDoc Verdict(VerdictStatus status, string? determination = null) => new()
    {
        Id = RecordIds.Verdict(P, "cas-zr", "bottle"), ProjectId = P,
        Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
        Dimensions = [new("ElementGate", status, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true,
        Determination = determination,
        DeterminationReason = determination is null ? null : "operator ruled",
    };

    /// A project whose Regulatory stage has already run, in the exact state POST /amendments leaves it:
    /// `pending` with an attempt already counted, and the previous verdicts still on file.
    private static async Task SeedScreenedThenAmendedAsync(
        InMemoryRecordStore store, string regulatoryStatus = StageStatus.Pending, int attempts = 1)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        project.Stages[Stages.Pool].Status = StageStatus.Done;
        project.Stages[Stages.Discovery].Status = StageStatus.Done;
        project.Stages[Stages.Regulatory].Status = regulatoryStatus;
        project.Stages[Stages.Regulatory].Attempts = attempts;
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict(VerdictStatus.Pass, Determinations.Recommended));
    }

    // ---- Regulatory ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAmendmentReset_ReScreens_RatherThanSkippingOverTheVerdictsAlreadyOnFile()
    {
        // The bug this test exists for. `missing` was computed as "candidates with no verdict yet", so a
        // stage reset by an amendment found nothing missing and skipped — every market/application/
        // client-ban amendment silently re-ran NOTHING while the endpoint reported `rerun: [regulatory, ...]`.
        var (runner, store, agents, _, _) = Sut();
        await SeedScreenedThenAmendedAsync(store);
        agents.Regulatory = (_, _, _) => Task.FromResult(AgentRunResult<VerdictDoc>.Ok(Verdict(VerdictStatus.Fail)));

        await runner.RunAsync(P, default);

        Assert.Equal(1, agents.RegulatoryCalls);
        var verdict = Assert.Single(await store.GetVerdictsAsync(P));
        Assert.Equal(VerdictStatus.Fail, verdict.Overall);
    }

    [Theory]
    [InlineData(StageStatus.Done)]
    [InlineData(StageStatus.Running)]
    [InlineData(StageStatus.Failed)]
    [InlineData(StageStatus.NeedsReview)]
    public async Task OnlyPending_ReScreens_SoAnIdlePassNeverReRunsAFinishedStage(string status)
    {
        // `pending` WITH verdicts on file is the signature of an amendment reset and of nothing else. If any
        // other status re-screened, every idle pipeline pass over a finished project would re-run the whole
        // regulatory fan-out — minutes of Foundry time, and a fresh set of verdicts that clears the operator's
        // determinations for no reason at all.
        var (runner, store, agents, _, _) = Sut();
        await SeedScreenedThenAmendedAsync(store, regulatoryStatus: status);

        await runner.RunAsync(P, default);

        Assert.Equal(0, agents.RegulatoryCalls);
    }

    [Fact]
    public async Task TheRerunPutsTheChangedVerdictOnTheTrail_ByName()
    {
        // "2 verdicts changed" is not actionable. The operator has to be able to read which substance, in
        // which component, moved which way — without opening the record and comparing it to a memory of what
        // it said two days ago.
        var (runner, store, agents, runs, _) = Sut();
        await SeedScreenedThenAmendedAsync(store);
        agents.Regulatory = (_, _, _) => Task.FromResult(AgentRunResult<VerdictDoc>.Ok(Verdict(VerdictStatus.Fail)));

        await runner.RunAsync(P, default);

        var steps = await StepsAsync(runs, Stages.Regulatory);
        Assert.Contains("Zr (cas-zr) in 'bottle': Pass -> Fail.", steps);
        Assert.Contains(steps, s => s.Contains("verdicts changed"));
    }

    [Fact]
    public async Task ARerunThatClearsTheOperatorsRuling_SaysSoOnTheTrail()
    {
        // The fresh VerdictDoc an agent writes carries `Determination = null`, so a rerun UN-SIGNS a ruling the
        // operator made. Discovering that three screens away, when the regulatory gate quietly refuses to arm,
        // is how a rerun becomes worse than no rerun.
        var (runner, store, agents, runs, _) = Sut();
        await SeedScreenedThenAmendedAsync(store);
        agents.Regulatory = (_, _, _) => Task.FromResult(AgentRunResult<VerdictDoc>.Ok(Verdict(VerdictStatus.Pass)));

        await runner.RunAsync(P, default);

        Assert.Contains("Zr (cas-zr) in 'bottle': determination recommended -> not ruled.",
            await StepsAsync(runs, Stages.Regulatory));
    }

    [Fact]
    public async Task AFirstScreen_EmitsNoDiffAtAll()
    {
        // A first run has nothing to compare against and nothing to warn about. Emitting "no prior state to
        // compare" on every project's first screen would make the line ordinary — and the whole value of that
        // sentence is that it is unusual enough to read.
        var (runner, store, _, runs, _) = Sut();
        await store.UpsertProjectAsync(
            ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement));

        await runner.RunAsync(P, default);

        var steps = await StepsAsync(runs, Stages.Regulatory);
        Assert.NotEmpty(steps);                                            // the stage really did run
        Assert.DoesNotContain(steps, s => s.Contains("Reran"));
        Assert.DoesNotContain(steps, s => s.Contains("prior state"));
    }

    // ---- Dosing ----------------------------------------------------------------------------------------

    /// A finalized dosing whose ONLY variable is Zr's order amount — the ppms, the windows and the second
    /// marker are identical between calls. TWO markers because a code is 2–3 by definition (RatioSignature
    /// refuses to form a ratio from one), so a one-marker fixture would not survive the store's round-trip.
    private static DosingDoc DosingWith(double zrCompoundMassMg) => new()
    {
        Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "2026-08-06T00:00:00Z",
        Windows =
        [
            new("bottle", "cas-zr", "Zr",
                new Bound(6, "measured background", BoundKinds.Measured, 1),
                new Bound(500, "no cap found", BoundKinds.Estimate, 0.5), 40, 12),
            new("bottle", "cas-y", "Y",
                new Bound(3, "device LOD", BoundKinds.Measured, 1),
                new Bound(500, "no cap found", BoundKinds.Estimate, 0.5), 20, 6),
        ],
        Codes =
        [
            new("bottle",
                [
                    new CodeMarker("cas-zr", "Zr", 40, 0.74, zrCompoundMassMg * 0.74, zrCompoundMassMg),
                    new CodeMarker("cas-y", "Y", 20, 0.78, 78, 100),
                ], "why"),
        ],
    };

    private static async Task SeedDosedThenAmendedAsync(
        InMemoryRecordStore store, InMemoryKnowledgeStore knowledge, DosingDoc prior)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var stage in new[] { Stages.Intake, Stages.Pool, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
            project.Stages[stage].Status = StageStatus.Done;
        // The amendment's reset: Dosing back to `pending`, with the attempt from its first run still counted.
        project.Stages[Stages.Dosing].Status = StageStatus.Pending;
        project.Stages[Stages.Dosing].Attempts = 1;
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(Constraints());
        await store.UpsertCandidatesAsync(Candidates());
        await store.UpsertVerdictAsync(Verdict(VerdictStatus.Pass, Determinations.Recommended));
        await store.UpsertDosingAsync(prior);
        await knowledge.UpsertSubstancePropertyAsync(new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("cas-zr"), Cas = "cas-zr", Element = "Zr", Form = "neodecanoate",
            MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-08-06T00:00:00.0000000+00:00",
        });
    }

    [Fact]
    public async Task ABatchMassRerun_ReportsTheOrderAmount_EvenThoughNoPpmMoved()
    {
        // `batchMassKg` is the ONLY amendment whose blast radius is Dosing alone, and it is a pure multiplier
        // on the order amount — it moves no ppm. A diff watching only the ppm windows would answer "nothing
        // changed" to the one amendment guaranteed to land on this stage: a confident all-clear over a
        // procurement quantity that just doubled.
        var (runner, store, agents, runs, knowledge) = Sut();
        await SeedDosedThenAmendedAsync(store, knowledge, DosingWith(200));
        agents.Dosing = (_, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(DosingWith(400)));

        await runner.RunAsync(P, default);

        Assert.Contains("Zr (cas-zr) in 'bottle': order amount 200 mg -> 400 mg.",
            await StepsAsync(runs, Stages.Dosing));
    }

    [Fact]
    public async Task ARerunThatChangedNothing_SaysSoOutLoud_RatherThanFallingSilent()
    {
        // Silence and "nothing changed" are different messages, and the operator cannot tell them apart. If
        // the unchanged case emitted no line, an operator who amended a requirement would be left unable to
        // distinguish "your sign-off still describes this record" from "the rerun never ran".
        var (runner, store, agents, runs, knowledge) = Sut();
        await SeedDosedThenAmendedAsync(store, knowledge, DosingWith(200));
        agents.Dosing = (_, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(DosingWith(200)));

        await runner.RunAsync(P, default);

        Assert.Contains("Reran; no dosing entry changed.", await StepsAsync(runs, Stages.Dosing));
    }
}
