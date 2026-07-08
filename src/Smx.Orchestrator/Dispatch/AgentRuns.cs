using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<VerdictDoc>> RunScreeningAsync(ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct) =>
        ScreeningAgent.RunAsync(
            new MafAgent(chatClient, ScreeningAgent.AgentName, ScreeningAgent.Instructions, toolBox.ScreeningTools()),
            constraints, substance, componentId, ct);
}
