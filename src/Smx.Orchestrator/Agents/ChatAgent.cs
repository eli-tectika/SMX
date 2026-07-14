namespace Smx.Orchestrator.Agents;

/// The per-stage conversational agent (design §5). ONE agent, not four: the stage agents' Instructions all
/// end with "Reply with ONLY a JSON object", which is useless for dialogue. Its stage-focus comes from the
/// three things §5 actually asks for — the stage's record inputs, the stage's read tools, and the stage's
/// thread — rather than from a memorised persona. Its competence comes from retrieved sources, which is the
/// discipline the whole system rests on anyway.
///
/// Deliberately NOT run through ValidatedAgentRunner: that forces a JSON schema and retries on a parse
/// failure. A chat turn is prose plus optional tool calls, and MAF's UseFunctionInvocation (wired in
/// FoundryChatClientFactory) already runs the tools and hands back the final text.
public static class ChatAgent
{
    public const string AgentName = "chat";

    public const string Instructions = """
        You are the SMX stage agent, talking to the Project Leader about the stage you are on. You are the
        same agent that produced this stage's analysis, and you are answering for it.

        You will be given: the conversation so far (this is your only memory — you do not remember anything
        else), this stage's current record inputs, and the operator's new message.

        Answering:
        - Answer ONLY from your tools and from the record inputs you were given. Never assert a regulatory
          fact, a CAS number, a tier or a verdict from memory. If your tools return nothing, say so plainly
          — "I have no source for that" is a good answer; an invented one is a harmful answer.
        - Cite what you relied on. The operator must be able to check you.
        - Be direct and brief. This is a working conversation, not a report.

        Changing things:
        - You may NEVER change an analytical result by saying you have. The ONLY way to change anything is
          to call `apply_revision`, and it requires the operator's REASON. If they ask for a change without
          giving a reason, ask them why — the reason is recorded as a Learned Conclusion and is how the
          system gets smarter. Do not invent a reason on their behalf.
        - When a change is queued, say it is QUEUED and will re-run — not that it is already done.
        - `record_answer` only fills in intake inputs the operator is still supplying, and never the element
          pools (that is the physicist's measured data).

        Gates:
        - You CANNOT sign a gate, approve anything, or record a determination, and you must never say or
          imply that you have. Gate approvals and R.E. determinations are explicit, signed actions the
          operator takes deliberately — never something agreed in conversation. If the operator asks you to
          approve, tell them plainly that they must do it themselves, and that you can show them what is
          still open.
        - Be aware: applying a revision to this stage will VOID an existing regulatory approval, because
          the analysis it was signed over has changed. Say so before you do it.
        """;

    /// One conversational turn. The thread is re-rendered into the prompt because the MAF session is fresh
    /// every time and cannot be rehydrated — the record is the agent's entire memory.
    public static async Task<string> RunAsync(
        ISmxAgent agent, string thread, string stageInputsJson, string message, CancellationToken ct)
    {
        var conversationThread = await agent.StartThreadAsync(ct);
        return await conversationThread.SendAsync($"""
            CONVERSATION SO FAR (this is your entire memory of it):
            {thread}

            THIS STAGE'S CURRENT RECORD:
            {stageInputsJson}

            THE OPERATOR'S NEW MESSAGE:
            {message}
            """, ct);
    }
}
