namespace Smx.Orchestrator.Agents;

public interface ISmxAgent
{
    string Name { get; }
    /// Starts a fresh conversation. Subsequent SendAsync calls on the returned thread continue
    /// the same conversation (used to feed validation errors back to the agent).
    Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct);
}

public interface ISmxAgentThread
{
    Task<string> SendAsync(string message, CancellationToken ct);
}
