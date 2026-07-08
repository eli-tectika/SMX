using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class ScriptedAgent(params string[] responses) : ISmxAgent, ISmxAgentThread
{
    private int _i;
    public string Name => "scripted";
    public List<string> Received { get; } = [];
    public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) => Task.FromResult<ISmxAgentThread>(this);
    public Task<string> SendAsync(string message, CancellationToken ct)
    {
        Received.Add(message);
        return Task.FromResult(responses[Math.Min(_i++, responses.Length - 1)]);
    }
}
