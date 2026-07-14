using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Domain.Tools;
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
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    /// TODO(search-proxy plan, Task 14): pass the project's real SensitiveTerms. A ConstraintsDoc carries no
    /// client or product name, so there is nothing here to build them from yet — Task 14 plumbs the ProjectDoc
    /// down to this call. Until it does, search_web is handed an EMPTY term list: the guard cannot block a
    /// leak it was never told about, which is precisely why SensitiveTerms is a required parameter.
    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions,
                toolBox.DiscoveryTools(SensitiveTerms.None)),
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
}
