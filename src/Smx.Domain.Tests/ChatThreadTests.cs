using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class ChatThreadTests
{
    [Fact]
    public void Render_ProducesAnOrderedTranscript_AttributedToSpeaker()
    {
        var turns = new List<ChatTurn>
        {
            new("operator", "why is Ba tier A?", "2026-07-13T01:00:00Z", []),
            new("agent", "The catalog lists it clean.", "2026-07-13T01:00:05Z",
                [new ChatToolCall("search_catalog", "element=Ba", null)]),
            new("operator", "and for HDPE?", "2026-07-13T01:01:00Z", []),
        };

        var rendered = ChatThread.Render(turns);

        // The agent gets a fresh MAF session every turn — this string is the ONLY memory it has.
        Assert.Contains("Operator: why is Ba tier A?", rendered);
        Assert.Contains("You: The catalog lists it clean.", rendered);
        Assert.Contains("and for HDPE?", rendered);
        // Order is the meaning: a transcript out of order lies about who said what first.
        Assert.True(rendered.IndexOf("why is Ba tier A?", StringComparison.Ordinal)
                  < rendered.IndexOf("and for HDPE?", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_OnAnEmptyThread_SaysSo_RatherThanReturningNothing()
    {
        // An empty string here would leave the prompt with a dangling "conversation so far:" header and
        // invite the model to invent a history. Say plainly that this is the first turn.
        Assert.Contains("first message", ChatThread.Render([]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_CarriesTheAgentsOwnToolCalls_SoItCanSeeWhatItAlreadyDid()
    {
        // Without this, a fresh session would re-run the same lookups every turn and could contradict a
        // citation it gave a moment ago — the operator would be talking to an agent with amnesia.
        var rendered = ChatThread.Render([
            new(ChatRoles.Agent, "Ba is clean.", "2026-07-13T01:00:00Z",
                [new ChatToolCall("search_catalog", "element=Ba", null)]),
        ]);
        Assert.Contains("search_catalog", rendered);
    }

    [Fact]
    public void Render_UsesTheWireRoleValues_PinnedToTheConstants()
    {
        // Pin the wire values themselves (not just the constants) — ChatRoles.Operator/.Agent are the
        // literal strings persisted on ChatTurn.Role, and a silent rename of either breaks every stored
        // thread. This test fails if "operator"/"agent" ever drift.
        Assert.Equal("operator", ChatRoles.Operator);
        Assert.Equal("agent", ChatRoles.Agent);
    }
}
