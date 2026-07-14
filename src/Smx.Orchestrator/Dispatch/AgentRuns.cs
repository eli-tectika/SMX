using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);

    /// <param name="revision">null for an ordinary run; non-null re-runs the stage applying the operator's
    /// revise-with-reason. Explicit rather than an overload: forgetting it is a compile error, not an agent
    /// that silently ignores the operator.</param>
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct);

    /// <param name="revision">null for an ordinary run; non-null re-screens the cell applying the revision.</param>
    Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct);

    Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct);

    /// One chat turn. Returns the agent's reply text; the tool-call trail is collected by the ChatTools
    /// instance the caller passes in (it is bound to this project + stage, so the model cannot name another).
    Task<string> RunChatAsync(string stage, ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions, toolBox.DiscoveryTools()),
            constraints, revision, ct);

    public Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct) =>
        RegulatoryAgent.RunAsync(
            new MafAgent(chatClient, RegulatoryAgent.AgentName, RegulatoryAgent.Instructions, toolBox.RegulatoryTools()),
            constraints, candidate, revision, ct);

    /// No tools: the distiller reasons only over what it is handed (the revision + the stage output it
    /// produced). Giving it search tools would let it "support" the conclusion with evidence the revision
    /// never rested on.
    public Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(
        RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct) =>
        ConclusionAgent.RunAsync(
            new MafAgent(chatClient, ConclusionAgent.AgentName, ConclusionAgent.Instructions, []),
            revision, constraints, stageOutputJson, ct);

    public Task<string> RunChatAsync(string stage, ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct) =>
        ChatAgent.RunAsync(
            new MafAgent(chatClient, ChatAgent.AgentName, ChatAgent.Instructions, ChatTurnTools(toolBox, chatTools, stage)),
            thread, stageInputsJson, message, ct);

    /// Everything a chat turn can DO: the stage's READ tools (so it answers for its stage from that stage's
    /// own sources) plus THIS turn's MUTATING tools (bound to this project + stage + chat message, so the
    /// model cannot name another project's analysis).
    ///
    /// What is deliberately NOT in this list: anything that could sign a gate, approve a stage, or record an
    /// R.E. determination. No such tool exists in ToolBox or ChatTools, so chat cannot approve anything — the
    /// capability is absent, not merely forbidden by the Instructions (Law 9: gates are operator-signed
    /// records, never voice-committed). An agent acts only through its tools; this list is the whole of it.
    ///
    /// A named, public function rather than an inline collection expression precisely because it is the whole
    /// of it: FakeAgentRuns replaces the entire run, so nothing else in the suite can observe this list, and a
    /// tool silently added to — or dropped from — it would otherwise be invisible until production.
    public static IList<AITool> ChatTurnTools(ToolBox toolBox, ChatTools chatTools, string stage) =>
        [.. toolBox.ReadToolsFor(stage), .. chatTools.Tools()];
}
