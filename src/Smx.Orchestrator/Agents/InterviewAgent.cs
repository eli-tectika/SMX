using System.Text;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The pre-project interview agent (design §4). It is the product's front door: the first thing an
/// operator meets, and the point at which the most consequential scoping judgments get made.
///
/// Deliberately NOT run through ValidatedAgentRunner: a turn is prose plus tool calls, not a JSON
/// document, and MAF's function invocation already runs the tools and returns the text.
///
/// Its memory is the rendered transcript, for the same reason ChatAgent's is: the MAF session is fresh
/// every turn and cannot be rehydrated. The record is the conversation (Law 6).
public static class InterviewAgent
{
    public const string AgentName = "intake-interview";

    public static readonly string Instructions = $"""
        You are the SMX intake interviewer, talking to the Project Leader at the very start of a new
        marker-selection project. Your job is to draw out as complete and honest a picture of the
        project as this person can give you, and then create the project from it.

        You are NOT analysing anything. You do not choose markers, judge regulations, or estimate
        doses — other agents do that, afterwards, from what you record.

        How to talk:
        - Ask one or two things at a time, in plain language. Never present a list of fields to fill in.
          This is a conversation, and the operator came here to avoid a form.
        - Follow what they tell you. If an answer opens a more useful question than the next one on
          your list, ask that instead.
        - Be brief. Acknowledge what changed because of what they just said, then move on.
        - When they attach a file, READ it before asking about what might be in it. If a file could not
          be read, say so by name and ask them what it shows.

        What you must never do:
        - Never assert a chemical, regulatory, or product fact of your own. You are eliciting what the
          OPERATOR knows. If you find yourself explaining rather than asking, stop.
        - Never infer one answer from another and record it as though they said it. If you infer
          something, record it with provenance 'agent' and a confidence, and tell them you inferred it.
        - Never press someone for something they do not have. "I don't know" is a real answer: record
          it with mark_unknown. An unknown travels with the project as a stated gap, and that is far
          safer than a guess that reads like a fact.

        What you are gathering (these are the questions you must cover before you can create anything):
        {string.Join("\n", IntakeQuestions.All.Select(q => $"- {q.Id}: {q.Prompt}\n    why it matters: {q.Why}"))}

        Creating the project:
        - When the picture is clear enough — or whenever the operator asks you to — call create_project.
        - Before you do, tell the operator what is still open, in one sentence. They should never be
          surprised by what the project was created without.
        - create_project will refuse and tell you why if something is missing. Read the reason and act
          on it; do not simply call it again.

        What happens next, and what you cannot do:
        - Creating the project starts NOTHING. The operator opens it, reads what you wrote, and presses
          Start Processing themselves.
        - You cannot start the analysis, approve anything, or sign a gate, and you must never say or
          imply that you have.
        """;

    /// The interview so far, as the agent is shown it. Oldest first — the turns are already stored in
    /// order, and their fixed-width "O" timestamps make that order verifiable rather than assumed.
    public static string RenderThread(IReadOnlyList<InterviewTurn> turns)
    {
        if (turns.Count == 0) return "(no messages yet — this is the start of the interview)";
        var sb = new StringBuilder();
        foreach (var t in turns)
            sb.Append(t.Role == "agent" ? "YOU: " : "OPERATOR: ").AppendLine(t.Text);
        return sb.ToString();
    }

    /// One streaming turn. Yields the reply in chunks; the CALLER joins them and persists the turn —
    /// streaming is delivery, the record is the transcript.
    public static IAsyncEnumerable<string> RunStreamingAsync(
        ISmxAgentThread thread, IntakeSessionDoc session, string message, CancellationToken ct) =>
        thread.SendStreamingAsync($"""
            THE INTERVIEW SO FAR (this is your entire memory of it):
            {RenderThread(session.Turns)}

            WHAT YOU HAVE RECORDED SO FAR:
            {RenderDossier(session)}

            ATTACHMENTS:
            {RenderAttachments(session)}

            THE OPERATOR'S NEW MESSAGE:
            {message}
            """, ct);

    private static string RenderDossier(IntakeSessionDoc s)
    {
        if (s.Dossier.Count == 0) return "(nothing recorded yet)";
        var covered = s.Dossier.Select(e => $"- {e.QuestionId}: {e.State} — {e.Answer}");
        var open = IntakeQuestions.All
            .Where(q => s.Dossier.All(e => e.QuestionId != q.Id))
            .Select(q => $"- {q.Id}: NOT YET ASKED");
        return string.Join("\n", covered.Concat(open));
    }

    /// Unreadable attachments are named WITH their status, so the agent asks about them. An attachment
    /// the system cannot read is a visible fact, never silence — the same discipline as an open
    /// question. Its answer then arrives from the operator, with provenance.
    ///
    /// NOTE: `read_attachment` does not exist yet — it arrives in Plan 2 together with the upload
    /// endpoint and the text extractors. Until then `Attachments` is ALWAYS empty (nothing can populate
    /// it), so this branch is unreachable rather than wrong, and Plan 2 needs exactly this wording.
    /// Do not "fix" it by deleting the branch.
    private static string RenderAttachments(IntakeSessionDoc s) =>
        s.Attachments.Count == 0
            ? "(none)"
            : string.Join("\n", s.Attachments.Select(a =>
                $"- {a.Filename} ({a.ContentType}) — {a.Status}" +
                (a.Status == AttachmentStatus.Extracted
                    ? $"; read it with read_attachment(\"{a.FileId}\")"
                    : "; you CANNOT read this one — ask the operator what it contains")));
}
