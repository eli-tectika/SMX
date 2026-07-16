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

    /// The web URLs the model retrieved via a HOSTED web-search tool during the most recent SendAsync.
    /// Empty for every thread with no hosted web tool — which is all of them except a Discovery run under
    /// WEB_SEARCH_PROVIDER=hosted (the legacy proxy tool stamps "web:" on its own hits, and no other agent
    /// has a web tool at all). Discovery reads this to re-stamp web-derived citations in code, so RAIL 1
    /// (web-only ⇒ ≤ Tier B, never preferred) rests on a URL the tool actually returned rather than on the
    /// model's self-reported citation source. Default = none, so no existing implementation must change.
    IReadOnlyCollection<string> LastTurnWebCitations => [];
}
