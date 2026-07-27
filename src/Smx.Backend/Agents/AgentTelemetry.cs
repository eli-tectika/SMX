namespace Smx.Backend.Agents;

/// The ActivitySource names an agent run traces on — the list Program.cs hands the tracer provider.
///
/// These are OBSERVED, not guessed: each was read off a real turn (a ChatClientAgent and an IChatClient
/// driven through MafAgent's own construction path) with an ActivityListener attached to everything. Both
/// carry the `Experimental.` prefix the agent framework currently ships, and both would change name if that
/// prefix is ever dropped — which is precisely why BackendTelemetryWiringTests asserts against the names the
/// SDK emits on today rather than against this array.
///
/// The Azure SDK's own sources (Azure.Core.Http, Azure.Identity.*, the Cosmos and Search clients) are NOT
/// listed: the Azure Monitor distro subscribes to those itself, and BackendTelemetryWiringTests pins that so
/// this list does not silently grow a duplicate of what the distro already does.
///
/// NEVER replace this with AddSource("*") — see the recursion documented at the call site in Program.cs.
public static class AgentTelemetry
{
    public static readonly string[] Sources =
    [
        // Microsoft.Agents.AI — one span per agent turn (the MAF ChatClientAgent).
        "Experimental.Microsoft.Agents.AI",
        // Microsoft.Extensions.AI — one span per model call underneath it, carrying the model id,
        // token counts and finish reason. This is the span that shows a stage waiting on Foundry.
        "Experimental.Microsoft.Extensions.AI",
    ];
}
