using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, CancellationToken ct);
    Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions, toolBox.DiscoveryTools()),
            constraints, ct);

    public Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, CancellationToken ct) =>
        RegulatoryAgent.RunAsync(
            new MafAgent(chatClient, RegulatoryAgent.AgentName, RegulatoryAgent.Instructions, toolBox.RegulatoryTools()),
            constraints, candidate, ct);
}
