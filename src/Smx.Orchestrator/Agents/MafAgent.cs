using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Smx.Orchestrator.Agents;

/// Wraps a MAF <see cref="ChatClientAgent"/> (over our Foundry <see cref="IChatClient"/>) behind
/// <see cref="ISmxAgent"/>. All Microsoft.Agents.AI (MAF) SDK adaptation is confined to this file.
///
/// Microsoft.Agents.AI 1.13.0 surface used:
///   - Agent creation: new ChatClientAgent(IChatClient, instructions:, name:, tools:) (an AIAgent).
///   - Conversation/thread: AIAgent.CreateSessionAsync(ct) -> ValueTask&lt;AgentSession&gt; (a fresh session per StartThreadAsync).
///   - Run a turn: AIAgent.RunAsync(string message, AgentSession session, AgentRunOptions? options, ct) -> Task&lt;AgentResponse&gt;.
///   - Text extraction: AgentResponse.Text.
public sealed class MafAgent : ISmxAgent
{
    private readonly AIAgent _agent;
    public string Name { get; }

    public MafAgent(IChatClient chatClient, string name, string instructions, IList<AITool> tools)
    {
        Name = name;
        _agent = new ChatClientAgent(chatClient, instructions: instructions, name: name, tools: tools);
    }

    public async Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        return new AgentThreadAdapter(_agent, session);
    }

    /// The URLs a hosted web-search tool cited across these messages, pulled straight from their
    /// CitationAnnotations. This is the code-observed fact DiscoveryAgent.StampWebCitations re-stamps against —
    /// the tool's own record of what it fetched, not anything the model chose to write. Empty (and cheap) when
    /// the turn used no hosted web tool, which is every turn except a hosted Discovery run. internal + static,
    /// taking the messages rather than the AgentResponse, so it is driven directly by MafAgentTests.
    internal static IReadOnlyCollection<string> WebCitationUrls(IEnumerable<ChatMessage> messages)
    {
        HashSet<string>? urls = null;
        foreach (var message in messages)
            foreach (var content in message.Contents)
            {
                if (content.Annotations is null) continue;
                foreach (var annotation in content.Annotations)
                    if (annotation is CitationAnnotation { Url: { } url })
                        (urls ??= new(StringComparer.OrdinalIgnoreCase)).Add(url.ToString());
            }
        return urls ?? (IReadOnlyCollection<string>)[];
    }

    private sealed class AgentThreadAdapter(AIAgent agent, AgentSession session) : ISmxAgentThread
    {
        private IReadOnlyCollection<string> _lastTurnWebCitations = [];
        public IReadOnlyCollection<string> LastTurnWebCitations => _lastTurnWebCitations;

        public async Task<string> SendAsync(string message, CancellationToken ct)
        {
            var response = await agent.RunAsync(message, session, cancellationToken: ct).ConfigureAwait(false);
            _lastTurnWebCitations = WebCitationUrls(response.Messages);
            return response.Text;
        }
    }
}
