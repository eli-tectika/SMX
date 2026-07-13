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
    /// The line breaks a turn's text can arrive with. \r\n comes from anything pasted out of a Windows
    /// document; the lone \r is here so the set is exhaustive rather than merely likely.
    private static readonly string[] LineBreaks = ["\r\n", "\n", "\r"];

    public static string Render(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
            return "(This is the operator's first message in this stage — there is no prior conversation.)";

        var sb = new StringBuilder();
        foreach (var t in turns)
        {
            var speaker = t.Role == ChatRoles.Agent ? "You: " : "Operator: ";
            // EVERY line of the turn carries the speaker prefix, not just the first. Pasting an R.E.
            // determination or a supplier email into chat is the expected workflow, and a pasted line
            // beginning "You:" would otherwise render as the agent's OWN prior turn — after which the agent
            // will defend a claim it never made, in a system whose premise is that every claim traces to a
            // cited source. Prefixing every line is total: no line of the transcript is unattributed, so no
            // text inside a turn can forge one. (An escape/strip list would be a blocklist, and eventually wrong.)
            foreach (var line in t.Text.Split(LineBreaks, StringSplitOptions.None))
                sb.Append(speaker).Append(line).Append('\n');

            // Show the agent what it already looked up. Without it, a fresh session re-runs the same
            // retrievals every turn and can contradict a citation it gave one message ago.
            //
            // This is the renderer's OWN line and carries no speaker prefix — so it is the one line the rule
            // above cannot protect, and its content is untrusted: a tool-call summary is built from the
            // operator's verbatim reason (ApplyRevision renders "{target} — {reason}"). A line break in that
            // reason would break out onto an unattributed line and forge an agent turn. Collapsing every
            // break keeps the trail to one line per turn, which is also what it is meant to be.
            if (t.ToolCalls.Count > 0)
                sb.Append("  (you called: ")
                  .Append(string.Join(", ", t.ToolCalls.Select(c => $"{OneLine(c.Tool)}({OneLine(c.Summary)})")))
                  .Append(")\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// Flattens text onto a single line. Total, not a blocklist: every line break becomes a space, so nothing
    /// interpolated into an unprefixed line of the transcript can start a new one.
    private static string OneLine(string text) =>
        string.Join(" ", text.Split(LineBreaks, StringSplitOptions.None));
}
