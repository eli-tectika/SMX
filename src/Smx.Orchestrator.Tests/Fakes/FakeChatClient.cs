using Microsoft.Extensions.AI;

namespace Smx.Orchestrator.Tests.Fakes;

/// The smallest IChatClient a MafAgent can be constructed over: it never leaves the process and replies with
/// a fixed text. Enough to exercise everything AgentRuns does BEFORE the model is reached — in particular the
/// tool set it builds, which is where the project's sensitive terms are bound.
public sealed class FakeChatClient(string reply = "{\"substances\":[]}") : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
