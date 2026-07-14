using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// THE acceptance proof of the knowledge loop, and the only test that joins its two halves.
///
/// Every other test in this suite exercises ONE side of the seam: the writer pushes a chunk (assert the
/// chunk), the reader renders a chunk (assert the render). Both stayed green while search_marker_library
/// shipped DEAD for a full release — its tool description told the model to pass a phrase that was a
/// substring of no field, so it answered "no matches" to every question anyone could ask it. A seam is not
/// covered by testing the two things it joins; it is covered by driving one end and observing the other.
///
/// So this test drives the REAL StageDispatcher and the REAL LearnedConclusionWriter on project 1, and then
/// asks the REAL AIFunction — the object the agent runtime actually invokes, schema and binding included —
/// a question on project 2, in an unrelated project's own words. Nothing in between is stubbed. Whatever it
/// asserts is a claim about production behaviour, and whatever it cannot see, production cannot see either.
public class RevisionRoundTripTests
{
    private const string P1 = "proj-1";

    private const string Reason = "barium overlaps the titanium K-beta line at our XRF settings";
    private const string Finding = "Barium sulfate is unsuitable for XRF-marked HDPE packaging where Ti is present.";

    /// Project 1's world, and the ONE index both projects share. The writer is the production
    /// LearnedConclusionWriter — Cosmos upsert, embed the projected content, ensure, push — so the only way
    /// a conclusion reaches `index` is the way a conclusion reaches Azure AI Search in production.
    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents,
        InMemoryKnowledgeStore Knowledge, FakeLearnedConclusionsIndex Index) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var knowledge = new InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        var conclusions = new LearnedConclusionWriter(
            knowledge, index, new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents, knowledge, index);
    }

    /// Project 1 mid-flight: a bottle component in HDPE, and Ba tiered A by Discovery. The operator is about
    /// to disagree with that tier for a reason no corpus contains — it is a fact about SMX's own instrument.
    private static async Task SeedProjectOneAsync(InMemoryRecordStore store)
    {
        await store.UpsertProjectAsync(ProjectDoc.Create(P1, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement));
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P1), ProjectId = P1,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "anti-counterfeit")],
            ElementPools = [new("bottle", "Ba", "Kα", "V", null)],
        });
        await store.UpsertCandidatesAsync(Candidates("A"));
    }

    private static CandidatesDoc Candidates(string tier) => new()
    {
        Id = RecordIds.Candidates(P1), ProjectId = P1,
        Substances = [new("bottle", "Ba", "sulfate", "cas-ba", null, null, true, tier, "revised",
            [new Citation("catalog", "ref-catalog/ba", "t")])],
    };

    /// Project 2 has no project record, no constraints and no shared conversation with project 1 — only the
    /// knowledge layer, which is exactly the reach a later project has. `learnedConclusions` sees nothing but
    /// what the writer pushed, through the two fields the production reader selects.
    private static ToolBox LaterProjectToolBox(InMemoryKnowledgeStore knowledge, FakeLearnedConclusionsIndex index)
    {
        var search = new FakeSearch();
        return new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            knowledge, new IndexBackedLearnedConclusionsSearch(index), _ => new FakeWebSearch());
    }

    [Fact]
    public async Task AnOperatorsReasonInOneProject_ReachesAnUnrelatedAgentInTheNext()
    {
        var (dispatcher, store, agents, knowledge, index) = Sut();
        await SeedProjectOneAsync(store);

        // PROJECT 1 — the operator revises Discovery with a reason.
        agents.Discovery = (_, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(Candidates("C")));
        agents.Conclusion = (_, _, _) => Task.FromResult(AgentRunResult<ConclusionOutput>.Ok(new ConclusionOutput
        {
            // The distiller GENERALIZES: this project was one bottle, but the finding is about Ba-sulfate in
            // HDPE packaging — which is what makes it reusable by a project that shares no component with it.
            Scope = new("Ba", "sulfate", "HDPE", "packaging", null, null),
            Finding = Finding,
            Confidence = 0.7,
        }));

        await dispatcher.OnRecordChangedAsync(new RevisionDoc
        {
            Id = RecordIds.Revision(P1, Stages.Discovery, "rev1"), ProjectId = P1, Stage = Stages.Discovery,
            Target = "Ba tier", Reason = Reason,
            CreatedAt = "2026-07-13T10:00:00.0000000+00:00",
        }, default);

        // The revision landed as a revision: the tier actually changed. If it did not, the conclusion below
        // would be knowledge filed for a decision that never took effect.
        Assert.Equal("C", Assert.Single((await store.GetCandidatesAsync(P1))!.Substances).Tier);

        // PROJECT 2 — a later, unrelated agent asks about Ba IN ITS OWN WORDS, through the real AIFunction.
        // Not the C# method: a tool's JSON schema can lie (search_marker_library emitted as REQUIRED the very
        // params its description told the model to omit), and a method-level call would never notice. This is
        // the object the runtime hands the model.
        var tool = Assert.IsAssignableFrom<AIFunction>(
            LaterProjectToolBox(knowledge, index).DiscoveryTools(Smx.Domain.Tools.SensitiveTerms.None).Single(t => t.Name == "search_learned_conclusions"));
        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "is barium safe to tier for an HDPE packaging component?",
        }))?.ToString() ?? "";

        // The sentinel is the failure mode this whole test exists to catch: it is what a dead seam returns,
        // and it reads to the model as "nobody has ever learned anything about this".
        Assert.DoesNotContain("no matches", result);

        Assert.Contains(Finding, result);          // the distilled, generalized finding
        Assert.Contains("0.70", result);           // the calibrated confidence the instructions tell it to weigh
        Assert.Contains("recorded:", result);      // recency, so a newer conclusion can supersede an older one
        Assert.Contains(P1, result);               // the project it came from — a conclusion must be traceable

        // THE PAYLOAD OF THE ENTIRE LOOP. The distilled finding says Ba-sulfate is unsuitable; only the
        // operator's own words say WHY, and the why is a fact about SMX's instrument that exists in no corpus
        // on earth. Strip it and the next project relearns it the expensive way — by shipping a marker that
        // cannot be read.
        Assert.Contains(Reason, result);
    }

    [Fact]
    public async Task TheConclusionIsRetrievableOnlyBecauseItWasPushed_NotBecauseItWasStored()
    {
        // The negative control for the test above. Cosmos is authoritative but it is NOT the retrieval path:
        // an agent never reads a LearnedConclusionDoc, it reads an index chunk. Assert that the search double
        // is genuinely blind to the store — otherwise the round-trip test could pass with a writer that never
        // pushed anything, which is the exact drift LearnedConclusionWriter exists to prevent.
        var knowledge = new InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        await knowledge.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "rev-unpushed"),
            Kind = KnowledgeKinds.Material,
            Scope = new("Ba", "sulfate", "HDPE", "packaging", null, null),
            Finding = Finding,
            Confidence = 0.7,
            Provenance = new([P1], [$"operator reason: {Reason}"]),
            CreatedAt = "2026-07-13T10:00:00.0000000+00:00",
        });

        var tool = Assert.IsAssignableFrom<AIFunction>(
            LaterProjectToolBox(knowledge, index).DiscoveryTools(Smx.Domain.Tools.SensitiveTerms.None).Single(t => t.Name == "search_learned_conclusions"));
        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "is barium safe to tier for an HDPE packaging component?",
        }))?.ToString() ?? "";

        Assert.Contains("no matches", result);
        Assert.DoesNotContain(Reason, result);
    }
}
