using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Smx.Backend.Tests.Fakes;

/// The smallest IChatClient a MafAgent can be constructed over: it never leaves the process and replies with
/// a fixed text. Enough to exercise everything AgentRuns does BEFORE the model is reached — in particular the
/// tool set it builds, which is where the project's sensitive terms are bound.
///
/// <paramref name="streamingChunks"/> is optional: when null, GetStreamingResponseAsync still throws (the
/// original behavior, preserved for every existing caller that never streams); when supplied, it drives
/// GetStreamingResponseAsync with those chunks delivered one at a time, so a streaming caller can be tested
/// without a real model.
public sealed class FakeChatClient(string reply = "{\"substances\":[]}", IReadOnlyList<string>? streamingChunks = null)
    : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        streamingChunks is null
            ? throw new NotSupportedException()
            : StreamAsync(streamingChunks, cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IReadOnlyList<string> chunks, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield(); // force real asynchrony so a caller can't accidentally assume synchronous delivery
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
