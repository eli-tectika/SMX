using System.Runtime.CompilerServices;

namespace Smx.Backend.Agents;

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

    /// A streaming turn: the same conversation as SendAsync, delivered incrementally.
    ///
    /// DEFAULTED so no existing implementation must change — every stage agent runs to completion and
    /// has no use for this. The default streams the finished reply as a single chunk, which is
    /// correct-but-unhelpful rather than unimplemented: a caller written against this interface works
    /// on every agent, and only the interview turn benefits.
    ///
    /// The CALLER is responsible for persisting the joined text. Streaming is a delivery detail; the
    /// record is still the transcript (Law 6 — the interview must survive a closed tab).
    async IAsyncEnumerable<string> SendStreamingAsync(
        string message, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await SendAsync(message, ct).ConfigureAwait(false);
    }

    /// The web URLs the model retrieved via a HOSTED web-search tool during the most recent SendAsync.
    /// Empty for every thread with no hosted web tool — which is all of them except a Discovery run under
    /// WEB_SEARCH_PROVIDER=hosted (the proxy tool stamps "web:" on its own hits, and no other agent has a
    /// web tool at all). Discovery reads this to re-stamp web-derived citations in code, so RAIL 1
    /// (web-only ⇒ ≤ Tier B, never preferred) rests on a URL the tool actually returned rather than on the
    /// model's self-reported citation source. Default = none, so no existing implementation must change.
    IReadOnlyCollection<string> LastTurnWebCitations => [];
}
