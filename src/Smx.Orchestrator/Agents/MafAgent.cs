using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Smx.Orchestrator.Agents;

/// Wraps a MAF <see cref="ChatClientAgent"/> (over our Foundry <see cref="IChatClient"/>) behind
/// <see cref="ISmxAgent"/>. All Microsoft.Agents.AI (MAF) SDK adaptation is confined to this file.
///
/// Microsoft.Agents.AI 1.13.0 surface used:
///   - Agent creation: new ChatClientAgent(IChatClient, instructions:, name:, tools:) (an AIAgent).
///   - Conversation/thread: AIAgent.CreateSessionAsync(ct) -> ValueTask&lt;AgentSession&gt; (a fresh session per StartThreadAsync).
///   - Run a turn: AIAgent.RunAsync(string message, AgentSession session, AgentRunOptions? options, ct) -> Task&lt;AgentResponse&gt;.
///   - Text extraction: AgentResponse.Text.
public sealed class MafAgent : ISmxAgent
{
    private readonly AIAgent _agent;
    public string Name { get; }

    public MafAgent(IChatClient chatClient, string name, string instructions, IList<AITool> tools)
    {
        Name = name;
        _agent = new ChatClientAgent(chatClient, instructions: instructions, name: name, tools: tools);
    }

    public async Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        return new AgentThreadAdapter(_agent, session);
    }

    private sealed class AgentThreadAdapter(AIAgent agent, AgentSession session) : ISmxAgentThread
    {
        public async Task<string> SendAsync(string message, CancellationToken ct)
        {
            var response = await agent.RunAsync(message, session, cancellationToken: ct).ConfigureAwait(false);
            return response.Text;
        }
    }
}
