using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;
using Smx.Backend.Knowledge;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The SDS master list as a LIVING LEDGER (2026-07-29 design §6, D7).
///
/// Before this, nothing a project did ever put a substance on the list: it was written only by the static
/// seed, so the sweep chased a catalog while the substances an actual project was about to dose were
/// invisible to it. Now a substance entering play is what enrolls it — at the two moments it enters:
/// Discovery mints a candidate with a CAS, and Dosing selects a marker.
///
/// The other half of the contract is that the enrollment is BOOKKEEPING. A missing ledger row costs a
/// later fetch; a failed stage costs the operator's day. So every test that proves the append happens is
/// paired with one proving the stage survives the append failing.
public class PipelineRunnerLedgerTests
{
    private const string P = "p1";

    private static (PipelineRunner Runner, InMemoryRecordStore Store, FakeAgentRuns Agents, FakeSdsAcquisition Sds)
        Sut(FakeSdsAcquisition? sds = null, InMemoryKnowledgeStore? knowledge = null)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var acquisition = sds ?? new FakeSdsAcquisition();
        var conclusions = new LearnedConclusionWriter(
            knowledge ?? new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(), conclusions, 2,
                    knowledge: knowledge, sds: acquisition),
                store, agents, acquisition);
    }

    private static async Task<ProjectDoc> Seed(InMemoryRecordStore store)
    {
        var doc = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    private static CandidateSubstance Candidate(string component, string element, string form, string cas) =>
        new(component, element, form, cas, null, null, false, "A", "ok", [new Citation("catalog", "x", "t")]);

    // ---- Discovery -------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryDiscoveredCandidateIsAppendedToTheSdsLedger()
    {
        var (d, store, agents, sds) = Sut();
        await Seed(store);
        agents.Discovery = (_, c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances =
            [
                Candidate("bottle", "Zr", "TMHD complex", "18865-74-2"),
                Candidate("bottle", "Y", "2-ethylhexanoate", "80326-98-3"),
            ],
        }));

        await d.RunAsync(P, default);

        Assert.Contains(sds.Appended, a => a.Cas == "18865-74-2" && a.Element == "Zr" && a.Form == "TMHD complex");
        Assert.Contains(sds.Appended, a => a.Cas == "80326-98-3" && a.Element == "Y" && a.Form == "2-ethylhexanoate");
    }

    // One substance is one row on the ledger no matter how many components carry it — the list is keyed by
    // (element, form), not by component. Appending per candidate row would post the same substance three
    // times for a three-component product.
    [Fact]
    public async Task TheSameSubstanceInSeveralComponentsIsAppendedOnce()
    {
        var (d, store, agents, sds) = Sut();
        await Seed(store);
        agents.Discovery = (_, c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances =
            [
                Candidate("bottle", "Zr", "TMHD complex", "18865-74-2"),
                Candidate("label", "Zr", "TMHD complex", "18865-74-2"),
                Candidate("lid", "Zr", "TMHD complex", "18865-74-2"),
            ],
        }));

        await d.RunAsync(P, default);

        Assert.Single(sds.Appended, a => a.Cas == "18865-74-2");
    }

    // A ledger append is bookkeeping. It must never be able to fail a stage — not even when the client
    // misbehaves and throws rather than swallowing its own transport failure.
    [Fact]
    public async Task AnAppendFailureDoesNotFailTheStage()
    {
        var (d, store, agents, _) = Sut(new FakeSdsAcquisition { ThrowOnAppend = true });
        await Seed(store);
        agents.Discovery = (_, c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances = [Candidate("bottle", "Zr", "TMHD", "18865-74-2")],
        }));

        await d.RunAsync(P, default);

        var project = store.Documents.OfType<ProjectDoc>().Single();
        Assert.Equal("done", project.Stages[Stages.Discovery].Status);
        Assert.Null(project.Stages[Stages.Discovery].Error);
        Assert.NotNull(await store.GetCandidatesAsync(P));   // and the candidates still landed
    }

    // A host with no SDS service wired (the client is an OPTIONAL dependency, exactly as `knowledge` and
    // `catalog` are) still runs every stage. The ledger simply does not fill.
    [Fact]
    public async Task WithNoSdsServiceWired_DiscoveryStillRuns()
    {
        var store = new InMemoryRecordStore();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        var runner = new PipelineRunner(store, new InMemoryRunStore(), new FakeAgentRuns(), new ThreadEventHub(),
            conclusions, 2);
        await Seed(store);

        await runner.RunAsync(P, default);

        Assert.NotNull(await store.GetCandidatesAsync(P));
    }

    // Known-candidate mode bypasses the Discovery AGENT, not the ledger: an operator-provided candidate is a
    // substance in play by exactly the same measure, and its sheet is needed by exactly the same order gate.
    [Fact]
    public async Task OperatorProvidedCandidatesAreAppendedToo()
    {
        var (d, store, agents, sds) = Sut();
        await Seed(store);
        agents.Intake = p => Task.FromResult(AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
            ProvidedCandidates = [Candidate("bottle", "Ba", "sulfate", "7727-43-7")],
        }));

        await d.RunAsync(P, default);

        Assert.Equal(0, agents.DiscoveryCalls);                                  // the agent never ran
        Assert.Contains(sds.Appended, a => a.Cas == "7727-43-7" && a.Form == "sulfate");
    }

    // ---- Dosing ----------------------------------------------------------------------------------------

    private static ConstraintsDoc DosingConstraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
        MeasuredBackgrounds = [new("bottle", "Zr", 5.0, "ppm"), new("bottle", "Y", 4.0, "ppm")],
        Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm"), new DeviceLod("Y", 1.5, "ppm")]),
    };

    /// A project screened, determined and SIGNED, with TWO compliant substances whose loadings are on file —
    /// i.e. fully doseable. Two because a code is 2–3 markers: its identity is the RATIO between them, and
    /// RatioSignature refuses to form one from a single marker. The candidates carry the FORM, which the
    /// dosed markers do not — that join is what the ledger append depends on.
    private static async Task SeedDoseableAsync(InMemoryRecordStore store, InMemoryKnowledgeStore knowledge)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "awaiting-RE";
        project.Stages[Stages.Matrix].Status = "done";
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(DosingConstraints());
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances =
            [
                Candidate("bottle", "Zr", "neodecanoate", "cas-ok"),
                Candidate("bottle", "Y", "2-ethylhexanoate", "cas-two"),
            ],
        });
        foreach (var (cas, element, form) in new[] { ("cas-ok", "Zr", "neodecanoate"), ("cas-two", "Y", "2-ethylhexanoate") })
        {
            await store.UpsertVerdictAsync(new VerdictDoc
            {
                Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
                Cas = cas, ComponentId = "bottle", Element = element, Form = form,
                Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
                EvidenceReviewed = true,
                Determination = Determinations.Recommended, DeterminationReason = "operator ruled",
            });
            await knowledge.UpsertSubstancePropertyAsync(new SubstancePropertyDoc
            {
                Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = form,
                MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
            });
        }
    }

    /// The code a scripted Dosing run returns: both compliant substances, in a real 3:2 ratio.
    private static MarkerCode Code(double zrPpm = 450.0, double yPpm = 300.0) =>
        new("bottle",
            [
                new CodeMarker("cas-ok", "Zr", zrPpm, 0.74, zrPpm * 10.0, zrPpm * 13.5),
                new CodeMarker("cas-two", "Y", yPpm, 0.74, yPpm * 10.0, yPpm * 13.5),
            ],
            "why");

    // A SELECTED marker is the strongest possible signal that we will need its sheet — the order gate is
    // three stages away and blocks on exactly this. Its form comes from the candidate it was minted as: a
    // CodeMarker carries CAS and element only, and the ledger is keyed by (element, form).
    [Fact]
    public async Task EverySelectedMarkerIsAppendedToTheSdsLedger()
    {
        var knowledge = new InMemoryKnowledgeStore();
        var (d, store, agents, sds) = Sut(knowledge: knowledge);
        await SeedDoseableAsync(store, knowledge);
        agents.Dosing = (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
            Codes = [Code()],
            GeneratedAt = "2026-07-15T00:00:00Z",
        }));

        await d.RunAsync(P, default);

        Assert.Contains(sds.Appended, a => a.Cas == "cas-ok" && a.Element == "Zr" && a.Form == "neodecanoate");
        Assert.Contains(sds.Appended, a => a.Cas == "cas-two" && a.Element == "Y" && a.Form == "2-ethylhexanoate");
    }

    [Fact]
    public async Task ADosingAppendFailureDoesNotFailTheStage()
    {
        var knowledge = new InMemoryKnowledgeStore();
        var (d, store, agents, _) = Sut(new FakeSdsAcquisition { ThrowOnAppend = true }, knowledge);
        await SeedDoseableAsync(store, knowledge);
        agents.Dosing = (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
            Codes = [Code()],
            GeneratedAt = "2026-07-15T00:00:00Z",
        }));

        await d.RunAsync(P, default);

        var project = store.Documents.OfType<ProjectDoc>().Single();
        Assert.Equal("done", project.Stages[Stages.Dosing].Status);
        Assert.Null(project.Stages[Stages.Dosing].Error);
        Assert.NotNull(await store.GetDosingAsync(P));
    }

    // One substance is one append even when it appears in several codes (or several components' codes).
    [Fact]
    public async Task AMarkerUsedInSeveralCodesIsAppendedOnce()
    {
        var knowledge = new InMemoryKnowledgeStore();
        var (d, store, agents, sds) = Sut(knowledge: knowledge);
        await SeedDoseableAsync(store, knowledge);
        agents.Dosing = (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
            Codes = [Code(), Code(zrPpm: 300.0, yPpm: 200.0)],
            GeneratedAt = "2026-07-15T00:00:00Z",
        }));

        await d.RunAsync(P, default);

        Assert.Single(sds.Appended, a => a.Cas == "cas-ok");
    }
}
