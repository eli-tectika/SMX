using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ChatAgentTests
{
    private static ToolBox Box() =>
        new(new FakeCatalogLookup(), new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(),
            new FakeSearch(), new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch(),
            _ => new FakeWebSearch());

    [Fact]
    public async Task Run_GivesTheAgentTheThread_TheStageInputs_AndTheNewMessage()
    {
        // The agent gets a FRESH MAF session every turn and there is no rehydration API. So everything it
        // knows must be in this one prompt — if the thread isn't in there, the operator is talking to an
        // agent with amnesia that will happily contradict the answer it gave a minute ago.
        var agent = new ScriptedAgent("Because the catalog lists it clean.");

        var reply = await ChatAgent.RunAsync(agent,
            thread: ChatThread.Render(
                [new ChatTurn("m1", ChatRoles.Operator, "why is Ba tier A?", "t1", [], ChatStatus.Answered)]),
            stageInputsJson: """{"substances":[{"element":"Ba","tier":"A"}]}""",
            message: "and for HDPE?",
            ct: default);

        var prompt = Assert.Single(agent.Received);
        Assert.Contains("why is Ba tier A?", prompt);        // the rehydrated thread
        Assert.Contains("\"tier\":\"A\"", prompt);           // the stage's record inputs
        Assert.Contains("and for HDPE?", prompt);            // the new message
        Assert.Equal("Because the catalog lists it clean.", reply);
    }

    // The stage-focus of the ONE chat agent is not a persona — it is the record inputs, the thread, and
    // THESE tools. Each set is exactly the set its own stage agent reasoned with, so chat can answer for
    // what the stage produced from the same sources; it can neither retrieve what the stage could not, nor
    // (see the absent names) write, approve, or sign anything.
    [Fact]
    public void ReadToolsFor_GivesEachStageItsOwnStageAgentsReadTools()
    {
        var box = Box();
        static string[] Names(IEnumerable<Microsoft.Extensions.AI.AITool> tools) =>
            tools.Select(t => t.Name).OrderBy(x => x).ToArray();

        Assert.Equal(Names(box.IntakeTools()), Names(box.ReadToolsFor(Stages.Intake)));
        // Chat for Discovery gets the READ surface (DiscoveryReadTools), NOT the autonomous run's tools:
        // search_web is deliberately excluded so the operator's conversational surface is never a second
        // web-egress trigger. Egress stays confined to the autonomous Discovery run.
        Assert.Equal(Names(box.DiscoveryReadTools()), Names(box.ReadToolsFor(Stages.Discovery)));
        Assert.DoesNotContain("search_web", Names(box.ReadToolsFor(Stages.Discovery)));
        Assert.Equal(Names(box.RegulatoryTools()), Names(box.ReadToolsFor(Stages.Regulatory)));
        Assert.Equal(Names(box.DosingReadTools()), Names(box.ReadToolsFor(Stages.Dosing)));
    }

    // Pinned literally, not derived: this is the whole capability surface a chat turn's READ half offers the
    // model. Adding a tool anywhere in ToolBox that reaches a stage's chat agent must break a test and be
    // argued for — an `approve_gate` or `sign_determination` slipped into a stage set is the one thing this
    // system cannot let pass (Law 9). The MUTATING half is pinned the same way in ChatTools/Task 9.
    [Fact]
    public void ReadToolsFor_ExposesOnlyRetrieval_NoWriteApproveOrSignAnywhere()
    {
        var box = Box();
        Assert.Equal(
            ["search_learned_conclusions", "search_marker_library", "search_reference", "search_regulatory"],
            box.ReadToolsFor(Stages.Intake).Select(t => t.Name).OrderBy(x => x));
        Assert.Equal(
            ["lookup_compatibility", "search_catalog", "search_learned_conclusions", "search_reference"],
            box.ReadToolsFor(Stages.Discovery).Select(t => t.Name).OrderBy(x => x));
        Assert.Equal(
            ["search_reference", "search_regulatory", "search_sds"],
            box.ReadToolsFor(Stages.Regulatory).Select(t => t.Name).OrderBy(x => x));
        // Dosing's read half is retrieval-only too — prior dosing conclusions and the reference corpus. The
        // deterministic calculators (Task 10) are not retrieval and are not here; nothing that writes,
        // approves or signs is either.
        Assert.Equal(
            ["search_learned_conclusions", "search_reference"],
            box.ReadToolsFor(Stages.Dosing).Select(t => t.Name).OrderBy(x => x));
    }

    // Matrix and Cost retrieve nothing. Matrix's output is derived from the record it is handed; Cost's is a
    // deterministic table lookup — neither has a corpus to search, so a chat turn on them can only answer
    // from the stage inputs in its prompt or say it has no source, which is the fail-closed answer. An
    // UNRECOGNISED stage gets the same treatment, and it is the one that keeps this guarantee honest: `dosing`
    // is now a KNOWN stage with two read tools, so an unknown string (not a real stage) is the true witness
    // that the default arm still fails closed.
    [Fact]
    public void ReadToolsFor_MatrixCostAndAnUnknownStage_GetNoTools()
    {
        Assert.Empty(Box().ReadToolsFor(Stages.Matrix));
        Assert.Empty(Box().ReadToolsFor(Stages.Cost));
        Assert.Empty(Box().ReadToolsFor("screening"));   // an unknown stage — the fail-closed default
    }

    // The FULL tool surface of a real chat turn — the only place read + mutating are put together, and a
    // place FakeAgentRuns can never reach (it fakes the whole run). Untested, dropping `chatTools.Tools()`
    // from that one line would leave every dispatch test green while the model silently LOST apply_revision:
    // the operator asks for a change and gives their reason, and the agent — told the tool is the only way
    // to change anything — either cannot comply or says it did. Both are Law 4 failures.
    private static IList<Microsoft.Extensions.AI.AITool> TurnTools(string stage) =>
        AgentRuns.ChatTurnTools(Box(), new ChatTools(new InMemoryRecordStore(), "p1", stage, "k1"));

    // EVERY stage, exhaustively — a Fact that pinned only some of them let a mutation that special-cased
    // REGULATORY (dropping its apply_revision) pass the whole suite. Regulatory is the stage where a chat
    // revision voids a signed gate, i.e. the one with the most consequence and the least excuse for being
    // the unpinned one. Intake is not revisable — it has produced no analytical result yet, so it gap-fills
    // with record_answer instead. Matrix has neither: nothing to retrieve and nothing of its own to revise,
    // so a Matrix chat turn holds NO tools at all and can only answer from the record it was handed.
    [Theory]
    [InlineData(Stages.Intake,
        new[] { "record_answer", "search_learned_conclusions", "search_marker_library", "search_reference", "search_regulatory" })]
    [InlineData(Stages.Discovery,
        new[] { "apply_revision", "lookup_compatibility", "search_catalog", "search_learned_conclusions", "search_reference" })]
    [InlineData(Stages.Regulatory,
        new[] { "apply_revision", "search_reference", "search_regulatory", "search_sds" })]
    [InlineData(Stages.Matrix, new string[0])]
    // Dosing IS revisable (a ppm change with a reason), so its turn adds apply_revision to its two read tools.
    [InlineData(Stages.Dosing,
        new[] { "apply_revision", "search_learned_conclusions", "search_reference" })]
    // Cost is deterministic and NOT revisable: no read tools, no apply_revision — a read-only Q&A over the
    // CostDoc in the prompt, holding no tools at all.
    [InlineData(Stages.Cost, new string[0])]
    public void ChatTurnTools_AreTheStagesReadTools_PlusThisTurnsMutatingTools(string stage, string[] expected) =>
        Assert.Equal(expected, TurnTools(stage).Select(t => t.Name).OrderBy(x => x));

    // The stage is read from ChatTools, never passed alongside it. If the two could diverge, a turn could
    // retrieve with Regulatory's corpus while its apply_revision wrote a DISCOVERY revision — a RevisionDoc
    // that looks perfectly legitimate on the bus and was screened against the wrong stage's sources. This
    // pins the single source of truth so a `stage` parameter cannot creep back in beside it.
    [Fact]
    public void ChatTurnTools_TakeTheStageFromTheToolsTheyMutateWith()
    {
        var chatTools = new ChatTools(new InMemoryRecordStore(), "p1", Stages.Regulatory, "k1");
        Assert.Equal(Stages.Regulatory, chatTools.Stage);
        Assert.Equal(
            ["apply_revision", "search_reference", "search_regulatory", "search_sds"],
            AgentRuns.ChatTurnTools(Box(), chatTools).Select(t => t.Name).OrderBy(x => x));
    }

    // Law 9, asserted where it is actually decided: over the model's WHOLE capability list for a turn. Chat
    // cannot sign a gate, approve, or record a determination because no such tool is reachable from here —
    // not because the Instructions say so. Prose cannot be tested; this can, and it fails the moment someone
    // adds the tool. Matrix is included because it is the stage nearest the final approval.
    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    [InlineData(Stages.Dosing)]
    [InlineData(Stages.Cost)]
    public void ChatTurnTools_ContainNothingThatCouldSignAGateOrApproveAnything(string stage)
    {
        string[] forbidden = ["approve", "gate", "sign", "determination", "finalize", "release"];
        foreach (var name in TurnTools(stage).Select(t => t.Name))
            Assert.False(
                forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)),
                $"chat has no gate capability by construction, but the '{stage}' turn offers a tool named '{name}'");
    }
}
