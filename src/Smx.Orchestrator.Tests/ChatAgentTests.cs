using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ChatAgentTests
{
    private static ToolBox Box() =>
        new(new FakeCatalogLookup(), new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(),
            new FakeSearch(), new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch());

    [Fact]
    public async Task Run_GivesTheAgentTheThread_TheStageInputs_AndTheNewMessage()
    {
        // The agent gets a FRESH MAF session every turn and there is no rehydration API. So everything it
        // knows must be in this one prompt — if the thread isn't in there, the operator is talking to an
        // agent with amnesia that will happily contradict the answer it gave a minute ago.
        var agent = new ScriptedAgent("Because the catalog lists it clean.");

        var reply = await ChatAgent.RunAsync(agent,
            thread: ChatThread.Render([new ChatTurn(ChatRoles.Operator, "why is Ba tier A?", "t1", [])]),
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
        Assert.Equal(Names(box.DiscoveryTools()), Names(box.ReadToolsFor(Stages.Discovery)));
        Assert.Equal(Names(box.RegulatoryTools()), Names(box.ReadToolsFor(Stages.Regulatory)));
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
    }

    // Matrix retrieves nothing — its output is derived from the record it is handed, so there is no source
    // to search. A chat agent with no tools can only answer from the stage inputs in its prompt or say it
    // has no source, which is the fail-closed answer. An unrecognised stage gets the same treatment: an
    // unknown stage is a bug upstream, and the safe response to a bug is no capability at all.
    [Fact]
    public void ReadToolsFor_MatrixAndAnUnknownStage_GetNoTools()
    {
        Assert.Empty(Box().ReadToolsFor(Stages.Matrix));
        Assert.Empty(Box().ReadToolsFor("dosing"));
    }
}
