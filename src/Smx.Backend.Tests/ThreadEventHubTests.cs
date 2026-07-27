using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

/// The hub is the accelerator, never the source of truth (design §7.2) — but a frame delivered to the
/// WRONG topic is not a lost frame, it is a lie: one project's run steps rendering inside another
/// project's timeline. These pin the routing rather than taking it on trust.
public class ThreadEventHubTests
{
    private static ThreadFrame Frame(string id) => new("step", id, new { runId = id });

    [Fact]
    public void Every_subscriber_on_a_topic_receives_the_frame()
    {
        var hub = new ThreadEventHub();
        using var a = hub.Subscribe("p1", Stages.Pool);
        using var b = hub.Subscribe("p1", Stages.Pool);

        hub.Publish("p1", Stages.Pool, Frame("r1.s1"));

        Assert.True(a.Reader.TryRead(out var toA));
        Assert.True(b.Reader.TryRead(out var toB));
        Assert.Equal("r1.s1", toA!.Id);
        Assert.Equal("r1.s1", toB!.Id);
    }

    [Fact]
    public void A_frame_never_crosses_project_or_stage()
    {
        var hub = new ThreadEventHub();
        using var otherProject = hub.Subscribe("p2", Stages.Pool);
        using var otherStage = hub.Subscribe("p1", Stages.Discovery);
        using var target = hub.Subscribe("p1", Stages.Pool);

        hub.Publish("p1", Stages.Pool, Frame("r1.s1"));

        Assert.True(target.Reader.TryRead(out _));
        Assert.False(otherProject.Reader.TryRead(out _));
        Assert.False(otherStage.Reader.TryRead(out _));
    }

    /// The common case, by a wide margin: the runner publishes on every step and nobody has the screen
    /// open. It must be a no-op, not a throw — a telemetry publish that failed a stage would invert the
    /// whole D9 contract.
    [Fact]
    public void Publishing_with_no_subscriber_is_a_no_op()
    {
        var hub = new ThreadEventHub();
        hub.Publish("p1", Stages.Pool, Frame("r1.s1")); // must not throw
    }

    [Fact]
    public void Disposing_one_subscription_leaves_its_sibling_receiving()
    {
        var hub = new ThreadEventHub();
        var closed = hub.Subscribe("p1", Stages.Pool);
        using var open = hub.Subscribe("p1", Stages.Pool);

        closed.Dispose();
        hub.Publish("p1", Stages.Pool, Frame("r1.s1"));

        Assert.False(closed.Reader.TryRead(out _));
        Assert.True(open.Reader.TryRead(out var received));
        Assert.Equal("r1.s1", received!.Id);
    }
}
