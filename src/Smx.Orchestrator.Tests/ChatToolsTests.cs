using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

/// The tools that let a chat turn CHANGE something — the security-critical surface of the conversational
/// layer. Three properties are load-bearing here and each has a test that fails loudly without it:
///
///   1. the model cannot name a project (no projectId in any tool schema ⇒ no cross-project write),
///   2. the model cannot sign a gate (no such tool exists — Law 9),
///   3. a replayed turn cannot queue a second revision (the revision id is derived from the chat key).
///
/// EVERY test drives the real AIFunction via InvokeAsync, never the C# method. The model does not call the
/// method; it calls the tool, through a JSON schema AIFunctionFactory generates — and that schema can
/// disagree with the C# signature (a parameter without a default is emitted as REQUIRED however the
/// description reads). A test that calls the method cannot see that, and this repo has already shipped a
/// tool that was dead on arrival behind exactly such a test.
public class ChatToolsTests
{
    private const string P = "p1";
    private const string Cas = "cas-zr";
    private const string Component = "bottle";
    private const string ChatKey = "aaaa1111";

    private static JsonElement Payload => JsonDocument.Parse("""
        {
          "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"] }],
          "elementPools": [{ "component": "bottle", "element": "Ti", "line": "K", "status": "V" }]
        }
        """).RootElement.Clone();

    /// A project that has reached Discovery: intake payload in place, candidates produced, one verdict —
    /// i.e. there IS an analytical result to revise. The store's docs are what the tools read to decide
    /// whether a change is even coherent, so seeding matters.
    private static async Task<InMemoryRecordStore> SeedAsync(bool withConstraints = true)
    {
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create(P, "Acme", "Bottle", Payload));
        if (withConstraints)
            await store.UpsertConstraintsAsync(new ConstraintsDoc
            {
                Id = RecordIds.Constraints(P), ProjectId = P,
                Components = [new ComponentSpec(Component, "HDPE", "packaging", ["EU"], "anti-counterfeit")],
            });
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new CandidateSubstance(Component, "Zr", "ZrO2", Cas, null, null, true, "A", "why", [])],
        });
        await store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, Cas, Component), ProjectId = P,
            Cas = Cas, ComponentId = Component, Element = "Zr", Form = "ZrO2",
        });
        return store;
    }

    private static ChatTools Tools(IRecordStore store, string stage, string chatKey = ChatKey) =>
        new(store, P, stage, chatKey);

    private static AIFunction Tool(ChatTools tools, string name) =>
        (AIFunction)tools.Tools().Single(t => t.Name == name);

    private static async Task<string> InvokeAsync(AIFunction tool, AIFunctionArguments args) =>
        (await tool.InvokeAsync(args))?.ToString() ?? "";

    // ---------------------------------------------------------------- the schema is the guard

    /// THE CROSS-PROJECT WRITE GUARD. If `projectId` were a tool PARAMETER, one hallucinated id would mutate
    /// a DIFFERENT project's analysis — silently, with no undo, and with nobody having a reason to look. The
    /// binding to a single project is a constructor argument precisely so the model's schema offers no way
    /// to name one, and this asserts the schema keeps that promise.
    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    public async Task TheToolSchemas_ExposeNoProjectId(string stage)
    {
        foreach (var tool in Tools(await SeedAsync(), stage).Tools().OfType<AIFunction>())
        {
            var schema = tool.JsonSchema.ToString();
            Assert.DoesNotContain("projectId", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("project_id", schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// Law 9: gates are operator-signed records, never voice-committed. An agent can only act through its
    /// tools, so chat cannot sign a gate — not because it was instructed not to, but because the capability
    /// does not exist. POST /projects/{id}/regulatory/approve stays the only writer of an approved GateDoc.
    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    public async Task NoStage_GetsAGateOrApprovalTool(string stage)
    {
        foreach (var name in Tools(await SeedAsync(), stage).Tools().Select(t => t.Name))
            foreach (var forbidden in new[] { "approve", "gate", "sign", "determination" })
                Assert.DoesNotContain(forbidden, name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Stages.Discovery, new[] { "apply_revision" })]
    [InlineData(Stages.Regulatory, new[] { "apply_revision" })]
    [InlineData(Stages.Intake, new[] { "record_answer" })]
    // Matrix is assembled deterministically from candidates + verdicts (RevisionEffects.IsRevisable is
    // false for it): there is no agent output to revise, so chat gets no write tool at all there.
    [InlineData(Stages.Matrix, new string[0])]
    public async Task EachStage_GetsExactlyTheToolsItMayUse(string stage, string[] expected)
    {
        var names = Tools(await SeedAsync(), stage).Tools().Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(expected.OrderBy(n => n).ToArray(), names);
    }

    // ---------------------------------------------------------------- apply_revision

    [Fact]
    public async Task ApplyRevision_WritesTheSameRevisionDocTheEndpointWrites()
    {
        var store = await SeedAsync();
        var result = await InvokeAsync(Tool(Tools(store, Stages.Regulatory), "apply_revision"),
            new AIFunctionArguments
            {
                ["target"] = "the Zr verdict on the bottle",
                ["reason"] = "the R.E. says PPWR does not apply to a non-food HDPE bottle",
                ["cas"] = Cas,
                ["componentId"] = Component,
            });

        var revision = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(Stages.Regulatory, revision.Stage);
        Assert.Equal("the Zr verdict on the bottle", revision.Target);
        Assert.Equal("the R.E. says PPWR does not apply to a non-food HDPE bottle", revision.Reason);
        Assert.Equal(Cas, revision.Cas);
        Assert.Equal(Component, revision.ComponentId);
        Assert.Equal(RevisionStatus.Pending, revision.Status);
        Assert.Equal(P, revision.ProjectId);
        Assert.NotEmpty(revision.CreatedAt);

        // The change feed runs it LATER. If the tool implied the change were already applied, the model
        // would tell the operator it was done — and the operator would believe a change that has not
        // happened, which is the whole failure mode record-as-bus creates.
        Assert.Contains("queued", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"applied\":true", result);
    }

    /// Law 4: a change without a reason is a silent edit — it mutates an analytical result and teaches the
    /// system nothing, because the reason IS the seed of the Learned Conclusion. Refused, and NOTHING is
    /// written: a half-written revision would still be dispatched by the change feed.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyRevision_WithoutAReason_IsRefused_AndWritesNothing(string reason)
    {
        var store = await SeedAsync();
        var result = await InvokeAsync(Tool(Tools(store, Stages.Discovery), "apply_revision"),
            new AIFunctionArguments { ["target"] = "drop the Ba candidate", ["reason"] = reason });

        Assert.Contains("reason", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.GetRevisionsAsync(P));
    }

    [Fact]
    public async Task ApplyRevision_WithoutATarget_IsRefused_AndWritesNothing()
    {
        var store = await SeedAsync();
        var result = await InvokeAsync(Tool(Tools(store, Stages.Discovery), "apply_revision"),
            new AIFunctionArguments { ["target"] = " ", ["reason"] = "the operator said so" });

        Assert.Contains("target", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.GetRevisionsAsync(P));
    }

    /// A regulatory verdict is per substance × component. The dispatcher must never have to guess which cell
    /// the operator meant, so an unnamed one is refused rather than queued against an arbitrary verdict.
    [Fact]
    public async Task ApplyRevision_OnRegulatory_WithoutCasAndComponent_IsRefused()
    {
        var store = await SeedAsync();
        var result = await InvokeAsync(Tool(Tools(store, Stages.Regulatory), "apply_revision"),
            new AIFunctionArguments { ["target"] = "the verdict", ["reason"] = "the R.E. disagrees" });

        Assert.Contains("cas", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.GetRevisionsAsync(P));
    }

    /// THE SCHEMA-BINDING TEST. A Discovery revision has no cas and no componentId, and this drives the real
    /// AIFunction with only `target` + `reason` — exactly as the model would. Without `= null` defaults on
    /// those two parameters AIFunctionFactory emits them as REQUIRED and the binding throws
    /// (ArgumentException: missing a value for the required parameter 'cas'), so revise-with-reason is dead
    /// on arrival for Discovery no matter how correct the C# method body is.
    [Fact]
    public async Task ApplyRevision_OnDiscovery_WorksWithNoCasOrComponentId()
    {
        var store = await SeedAsync();
        var result = await InvokeAsync(Tool(Tools(store, Stages.Discovery), "apply_revision"),
            new AIFunctionArguments
            {
                ["target"] = "drop the Ba candidate",
                ["reason"] = "Ba overlaps the Ti K-beta line in this component's background",
            });

        var revision = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(Stages.Discovery, revision.Stage);
        Assert.Null(revision.Cas);
        Assert.Null(revision.ComponentId);
        Assert.Contains("queued", result, StringComparison.OrdinalIgnoreCase);
    }

    /// The audit link (design §5: no silent mutations). The reply carries the trail so the UI can show which
    /// sentence of the conversation changed which record — a change the operator cannot trace back to what
    /// they said is indistinguishable from one the model invented.
    [Fact]
    public async Task ApplyRevision_RecordsItsWriteInTheTrail_WithTheRecordId()
    {
        var store = await SeedAsync();
        var tools = Tools(store, Stages.Discovery);
        await InvokeAsync(Tool(tools, "apply_revision"), new AIFunctionArguments
        {
            ["target"] = "drop the Ba candidate", ["reason"] = "it overlaps the Ti K-beta line",
        });

        var call = Assert.Single(tools.Trail);
        Assert.Equal("apply_revision", call.Tool);
        Assert.Contains("drop the Ba candidate", call.Summary);
        Assert.Contains("overlaps the Ti K-beta line", call.Summary);   // the operator's reason, verbatim
        Assert.Equal((await store.GetRevisionsAsync(P))[0].Id, call.RecordId);
    }

    [Fact]
    public async Task ApplyRevision_RefusalIsNotRecordedInTheTrail()
    {
        var store = await SeedAsync();
        var tools = Tools(store, Stages.Discovery);
        await InvokeAsync(Tool(tools, "apply_revision"),
            new AIFunctionArguments { ["target"] = "drop Ba", ["reason"] = "" });

        Assert.Empty(tools.Trail);
    }

    // ---------------------------------------------------------------- idempotency: the replayed turn

    /// THE AT-LEAST-ONCE TEST. The chat change feed guards on the message's Status (answer only if
    /// `pending`, then flip to `answered`), but the durable side effect — this RevisionDoc — lands BEFORE
    /// that flip. Kill the process in between and the feed redelivers the still-`pending` message, the turn
    /// re-runs, and the model calls apply_revision again with the same arguments.
    ///
    /// A revision id minted from a fresh Guid would then produce a SECOND, distinct RevisionDoc: the stage
    /// re-runs twice from ONE operator instruction and TWO Learned Conclusions land from one reason. Deriving
    /// the id from the chat message's key makes the replay an UPSERT of the same doc — the same property
    /// RecordIds.ChatReply and KnowledgeIds.RevisionConclusion already rely on.
    [Fact]
    public async Task ApplyRevision_ReplayedTurn_UpsertsOneRevision_NotTwo()
    {
        var store = await SeedAsync();
        var args = () => new AIFunctionArguments
        {
            ["target"] = "drop the Ba candidate", ["reason"] = "it overlaps the Ti K-beta line",
        };

        // The first delivery of the message, then — after a crash before the status flip — the redelivery.
        // A fresh ChatTools each time, because a redelivered turn is a fresh turn: same chatKey, new object.
        await InvokeAsync(Tool(Tools(store, Stages.Discovery), "apply_revision"), args());
        await InvokeAsync(Tool(Tools(store, Stages.Discovery), "apply_revision"), args());

        Assert.Single(await store.GetRevisionsAsync(P));
    }

    /// ...and the ordinal is what keeps that from over-correcting. One message may legitimately ask for two
    /// changes ("drop Ba, and re-tier Zr to A"), and the model then calls apply_revision twice IN THE SAME
    /// TURN. Those are two decisions and both belong in the audit trail — so within a turn the ids must
    /// differ, while across a replay of that turn they must repeat.
    [Fact]
    public async Task ApplyRevision_TwiceInOneTurn_WritesTwoDistinctRevisions()
    {
        var store = await SeedAsync();
        var tools = Tools(store, Stages.Discovery);
        var tool = Tool(tools, "apply_revision");

        await InvokeAsync(tool, new AIFunctionArguments
        {
            ["target"] = "drop the Ba candidate", ["reason"] = "it overlaps the Ti K-beta line",
        });
        await InvokeAsync(tool, new AIFunctionArguments
        {
            ["target"] = "re-tier Zr to A", ["reason"] = "the compatibility table backs it on HDPE",
        });

        var revisions = await store.GetRevisionsAsync(P);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, revisions.Select(r => r.Id).Distinct().Count());
        Assert.Equal(2, tools.Trail.Count);

        // And a replay of that same two-change turn still lands exactly those two revisions.
        var replay = Tools(store, Stages.Discovery);
        var replayTool = Tool(replay, "apply_revision");
        await InvokeAsync(replayTool, new AIFunctionArguments
        {
            ["target"] = "drop the Ba candidate", ["reason"] = "it overlaps the Ti K-beta line",
        });
        await InvokeAsync(replayTool, new AIFunctionArguments
        {
            ["target"] = "re-tier Zr to A", ["reason"] = "the compatibility table backs it on HDPE",
        });
        Assert.Equal(2, (await store.GetRevisionsAsync(P)).Count);
    }

    /// Two DIFFERENT messages must never collide onto one revision id — that would silently overwrite the
    /// earlier operator instruction (and its Learned Conclusion) with the later one.
    [Fact]
    public async Task ApplyRevision_FromTwoDifferentMessages_WritesTwoRevisions()
    {
        var store = await SeedAsync();
        foreach (var key in new[] { "aaaa1111", "bbbb2222" })
            await InvokeAsync(Tool(Tools(store, Stages.Discovery, key), "apply_revision"),
                new AIFunctionArguments { ["target"] = "drop Ba", ["reason"] = "same words, different turn" });

        Assert.Equal(2, (await store.GetRevisionsAsync(P)).Count);
    }

    // ---------------------------------------------------------------- record_answer

    /// Gap-fill only. Once intake has produced constraints, every downstream stage was screened against that
    /// derived scope, so an "answer" is no longer filling a blank — it is CHANGING an established input, and
    /// that must go through revise-with-reason so the reason is recorded and the analysis is re-run.
    [Fact]
    public async Task RecordAnswer_IsRefusedOnceIntakeHasProducedConstraints()
    {
        var store = await SeedAsync(withConstraints: true);
        var result = await InvokeAsync(Tool(Tools(store, Stages.Intake), "record_answer"),
            new AIFunctionArguments { ["field"] = "components.bottle.material", ["value"] = "PET" });

        Assert.Contains("apply_revision", result);
        var project = await store.GetProjectAsync(P);
        Assert.Contains("HDPE", project!.Payload.GetRawText());   // untouched
    }

    /// The element pools are the physicist's MEASURED XRF background — every candidate and every verdict in
    /// the project rests on them. A chat tool able to rewrite them would let a language model silently move
    /// the ground under the whole analysis, with no undo and no reason for anyone to look. They are
    /// unwritable by construction (IntakeAnswers is an allowlist), and this is the proof at the tool seam.
    [Fact]
    public async Task RecordAnswer_RefusesToWriteTheElementPools()
    {
        var store = await StalledIntakeAsync();
        var tools = Tools(store, Stages.Intake);
        var result = await InvokeAsync(Tool(tools, "record_answer"),
            new AIFunctionArguments { ["field"] = "elementPools", ["value"] = "Ti,Zr" });

        Assert.Contains("physicist", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(tools.Trail);
        var project = await store.GetProjectAsync(P);
        Assert.DoesNotContain("Zr", project!.Payload.GetRawText());
        // A REFUSED answer must not re-trigger intake either: the stage is left exactly as it was.
        Assert.Equal("needs-review", project.Stages[Stages.Intake].Status);
    }

    /// Intake stalled on a missing input — the state in which record_answer is the right tool: the agent
    /// asked for something, the operator has just said it in chat, and no constraints exist yet.
    private static async Task<InMemoryRecordStore> StalledIntakeAsync()
    {
        var store = await SeedAsync(withConstraints: false);
        var project = (await store.GetProjectAsync(P))!;
        project.Stages[Stages.Intake].Status = "needs-review";
        project.Stages[Stages.Intake].Error = "no objective for component 'bottle'";
        await store.UpsertProjectAsync(project);
        return store;
    }

    /// Setting Intake back to `pending` IS the re-trigger: the upsert is a change-feed event, and
    /// StageDispatcher.OnProjectAsync runs Intake exactly when the stage is `pending` AND no constraints
    /// exist — both of which hold here (the constraints check above is what guarantees the second).
    [Fact]
    public async Task RecordAnswer_PatchesThePayload_AndReopensIntakeSoTheAgentReRuns()
    {
        var store = await StalledIntakeAsync();
        var tools = Tools(store, Stages.Intake);
        var result = await InvokeAsync(Tool(tools, "record_answer"),
            new AIFunctionArguments { ["field"] = "components.bottle.objective", ["value"] = "anti-counterfeit" });

        var reloaded = (await store.GetProjectAsync(P))!;
        Assert.Contains("anti-counterfeit", reloaded.Payload.GetRawText());
        Assert.Equal("pending", reloaded.Stages[Stages.Intake].Status);
        Assert.Null(reloaded.Stages[Stages.Intake].Error);
        Assert.Contains("recorded", result, StringComparison.OrdinalIgnoreCase);

        var call = Assert.Single(tools.Trail);
        Assert.Equal("record_answer", call.Tool);
        Assert.Equal(reloaded.Id, call.RecordId);
    }

    [Fact]
    public async Task RecordAnswer_UnknownField_IsRefused_AndWritesNothing()
    {
        var store = await StalledIntakeAsync();
        var tools = Tools(store, Stages.Intake);
        var result = await InvokeAsync(Tool(tools, "record_answer"),
            new AIFunctionArguments { ["field"] = "components.bottle.tier", ["value"] = "A" });

        Assert.Contains("not an answerable field", result);
        Assert.Empty(tools.Trail);
        Assert.Equal("needs-review", (await store.GetProjectAsync(P))!.Stages[Stages.Intake].Status);
    }
}
