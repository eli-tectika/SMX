using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Tools;
using Smx.Backend.Agents;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// Was DosingRevisionCostTests, and the property it pinned has changed shape rather than gone.
///
/// It used to assert that a Dosing revision RESET the Cost stage, so the separate CostDoc would be re-audited
/// over the revised substance set. Without that reset the operator revised their codes and the cost audit
/// silently went on describing substances that were no longer in them.
///
/// The Cost stage is deleted (redesign spec §6) and the supply audit rides on the DosingDoc itself, which
/// makes that whole class of staleness UNREPRESENTABLE: the audit cannot be out of step with the codes
/// because it is written by the same upsert. What is worth testing now is that the revise path actually
/// re-audits rather than carrying the old array forward on the new document.
public class DosingRevisionSupplyTests
{
    private const string P = "p1";

    private static (PipelineRunner Runner, InMemoryRecordStore Store, FakeAgentRuns Agents,
                    FakeCatalogLookup Catalog, InMemoryKnowledgeStore Knowledge) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var catalog = new FakeCatalogLookup();
        var knowledge = new InMemoryKnowledgeStore();
        var conclusions = new LearnedConclusionWriter(
            knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(), conclusions, 2,
                    knowledge: knowledge, catalog: catalog),
                store, agents, catalog, knowledge);
    }

    private static CatalogCard Card(string cas, string element, string supplier, string refId) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, null, null);

    private static VerdictDoc Verdict(string cas, string element) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P, Cas = cas, ComponentId = "bottle",
        Element = element, Form = "f", EvidenceReviewed = true,
        Determination = Determinations.Recommended, DeterminationReason = "ruled",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
    };

    private static SubstancePropertyDoc Loading(string cas, string element) => new()
    {
        Id = KnowledgeIds.SubstanceProperty(cas), Cas = cas, Element = element, Form = "form",
        MetalLoading = 0.74, Basis = "supplier assay", EnteredAt = "2026-07-13T09:00:00.0000000+00:00",
    };

    /// The metal loading is NOT optional here. Without it Dosing drops the substance by name and writes no
    /// document at all -- correct behaviour, and exactly what made the first draft of these two tests fail.
    private async Task SeedAsync(InMemoryRecordStore store, InMemoryKnowledgeStore knowledge)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var s in new[] { Stages.Intake, Stages.Discovery, Stages.Regulatory, Stages.Matrix })
            project.Stages[s].Status = StageStatus.Done;
        await store.UpsertProjectAsync(project);

        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
            MeasuredBackgrounds = [new("bottle", "Zr", 5.0, "ppm")],
            Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]),
        });
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, false, "A", "ok", [])],
        });
        await store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-zr", "Zr"));
        await knowledge.UpsertSubstancePropertyAsync(Loading("cas-zr2", "Zr"));
    }

    [Fact]
    public async Task Dosing_WritesTheSupplyAudit_OnTheSameDocumentAsTheCodes()
    {
        // The staleness this design removes: one upsert carries both, so an audit describing a substance set
        // the codes no longer contain cannot be persisted.
        var (runner, store, agents, catalog, knowledge) = Sut();
        await SeedAsync(store, knowledge);
        catalog.Returns("Zr", Card("cas-zr", "Zr", "Acme Chemicals", "cat-1"));
        agents.Dosing = (c, dosable, floors, loadings, _) => Task.FromResult(
            AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId, GeneratedAt = "t",
                Codes = [new MarkerCode("bottle",
                    [new CodeMarker("cas-zr", "Zr", 100, 0.74, 1, 2),
                     new CodeMarker("cas-zr2", "Zr", 50, 0.74, 1, 2)], "r")],
            }));

        await runner.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        // Every marker in the finalized code is audited -- including the one with no catalog listing, which
        // must appear as a FINDING rather than be dropped.
        Assert.Contains(dosing!.Supply, a => a.Cas == "cas-zr" && a.Suppliers.Contains("Acme Chemicals"));
        Assert.Contains(dosing.Supply, a => a.Cas == "cas-zr2" && a.Risks.Contains("not-off-the-shelf"));
    }

    [Fact]
    public async Task Dosing_WithNoCatalog_LeavesSupplyEmpty_RatherThanFabricatingAnAudit()
    {
        // The degrade path RunCostAsync had, preserved: an absent catalog produces NO audit, never an audit
        // of zeroes. An empty Supply means "we do not know"; a fabricated one would mean "nobody sells it",
        // which is a finding nobody made.
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        var runner = new PipelineRunner(store, new InMemoryRunStore(), agents, new ThreadEventHub(),
            new LearnedConclusionWriter(knowledge, new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
                NullLogger<LearnedConclusionWriter>.Instance),
            2, knowledge: knowledge);   // catalog: null
        await SeedAsync(store, knowledge);

        await runner.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.Empty(dosing!.Supply);
    }
}
