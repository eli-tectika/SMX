using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Smx.Backend.Pipeline;

/// One SSE frame, already shaped for §7.2.
public sealed record ThreadFrame(string Event, string Id, object Data);

/// In-process fan-out from the runner to whoever is watching.
///
/// The runner and the SSE endpoint are in the SAME process now, which is the whole point of the merge:
/// no Cosmos tailing, no relay, no replica-affinity problem. The persisted trail remains the source of
/// truth and this is the accelerator — a subscriber that misses a frame catches up on reconnect via
/// `?since=`, so a dropped frame costs latency and never content.
public sealed class ThreadEventHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ThreadFrame>>> _topics = new();

    private static string Topic(string projectId, string stage) => $"{projectId}|{stage}";

    public sealed class Subscription(ChannelReader<ThreadFrame> reader, Action dispose) : IDisposable
    {
        public ChannelReader<ThreadFrame> Reader { get; } = reader;
        public void Dispose() => dispose();
    }

    public Subscription Subscribe(string projectId, string stage)
    {
        // Unbounded + a slow reader is bounded in practice by one operator on one screen; DropOldest
        // would silently lose a step, which is exactly the failure this feature exists to remove.
        var channel = Channel.CreateUnbounded<ThreadFrame>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();
        var subscribers = _topics.GetOrAdd(Topic(projectId, stage), _ => new());
        subscribers[id] = channel;
        return new Subscription(channel.Reader, () => subscribers.TryRemove(id, out _));
    }

    public void Publish(string projectId, string stage, ThreadFrame frame)
    {
        if (!_topics.TryGetValue(Topic(projectId, stage), out var subscribers)) return;
        foreach (var channel in subscribers.Values) channel.Writer.TryWrite(frame);
    }
}
