using System.Text;
using Smx.Domain.Records;

namespace Smx.Domain;

/// Renders the persisted per-stage conversation into the transcript the agent is given each turn.
///
/// This exists because MafAgent.StartThreadAsync creates a FRESH, in-memory AgentSession and there is no
/// way to rehydrate one from stored messages. So the agent has no memory of its own: this string is the
/// entire conversation, reconstructed from the record on every single turn. That is what lets the
/// operator close the app on Monday and pick the thread up on Thursday (Law 6), and what lets an
/// orchestrator restart mid-conversation without losing it.
public static class ChatThread
{
    public static string Render(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
            return "(This is the operator's first message in this stage — there is no prior conversation.)";

        var sb = new StringBuilder();
        foreach (var t in turns)
        {
            sb.Append(t.Role == ChatRoles.Agent ? "You: " : "Operator: ").AppendLine(t.Text);
            // Show the agent what it already looked up. Without it, a fresh session re-runs the same
            // retrievals every turn and can contradict a citation it gave one message ago.
            if (t.ToolCalls.Count > 0)
                sb.Append("  (you called: ")
                  .Append(string.Join(", ", t.ToolCalls.Select(c => $"{c.Tool}({c.Summary})")))
                  .AppendLine(")");
        }
        return sb.ToString().TrimEnd();
    }
}
