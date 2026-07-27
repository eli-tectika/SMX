using Smx.Backend.Agents;
using Smx.Backend.Pipeline;

namespace Smx.Backend.Tests.Fakes;

public sealed class ScriptedAgent(params string[] responses) : ISmxAgent, ISmxAgentThread
{
    private int _i;
    public string Name => "scripted";
    public List<string> Received { get; } = [];
    /// Settable rather than a constructor parameter: `params string[]` has to stay last, and every
    /// existing call site passes only responses.
    public IRunTrail Trail { get; set; } = NullRunTrail.Instance;
    public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) => Task.FromResult<ISmxAgentThread>(this);
    public Task<string> SendAsync(string message, CancellationToken ct)
    {
        Received.Add(message);
        return Task.FromResult(responses[Math.Min(_i++, responses.Length - 1)]);
    }
}
