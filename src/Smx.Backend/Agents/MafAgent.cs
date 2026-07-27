using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Agents;

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
    public IRunTrail Trail { get; }

    public MafAgent(IChatClient chatClient, string name, string instructions, IList<AITool> tools,
        IRunTrail? trail = null)
    {
        Name = name;
        Trail = trail ?? NullRunTrail.Instance;
        _agent = new ChatClientAgent(chatClient, instructions: instructions, name: name, tools: tools);
    }

    public async Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        return new AgentThreadAdapter(_agent, session, Trail);
    }

    /// One tool invocation the SDK actually performed, paired with its result.
    public sealed record ObservedToolCall(string Tool, string? Query, int? ResultCount);

    /// The tools the SDK actually invoked on this turn, paired with their results.
    ///
    /// Read from FunctionCallContent/FunctionResultContent — the SDK's own record of what it ran,
    /// exactly as WebCitationUrls reads the annotations above. Nothing the model asserted about
    /// itself reaches the trail through here.
    internal static IEnumerable<ObservedToolCall> ToolCalls(IEnumerable<ChatMessage> messages)
    {
        var materialized = messages as IReadOnlyCollection<ChatMessage> ?? [.. messages];
        var results = new Dictionary<string, string?>();
        foreach (var message in materialized)
            foreach (var content in message.Contents)
                if (content is FunctionResultContent result)
                    results[result.CallId] = result.Result?.ToString();

        foreach (var message in materialized)
            foreach (var content in message.Contents)
                if (content is FunctionCallContent call)
                {
                    // The first STRING argument, which is the query on every tool in ToolBox that takes
                    // one. Not named ("query") because the parameter names differ across tools, and not
                    // the whole argument bag because this is a display sentence, not a payload dump.
                    var query = call.Arguments?.Values.OfType<string>().FirstOrDefault();
                    results.TryGetValue(call.CallId, out var raw);
                    yield return new ObservedToolCall(call.Name, query, CountResults(raw));
                }
    }

    /// A hit count when the result is a JSON array; null otherwise. Never a guess — "6 hits" that
    /// was inferred rather than counted is a fabricated number in an audit trail.
    private static int? CountResults(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
        }
        catch (JsonException) { return null; }
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

    private sealed class AgentThreadAdapter(AIAgent agent, AgentSession session, IRunTrail trail) : ISmxAgentThread
    {
        private IReadOnlyCollection<string> _lastTurnWebCitations = [];
        public IReadOnlyCollection<string> LastTurnWebCitations => _lastTurnWebCitations;

        public async Task<string> SendAsync(string message, CancellationToken ct)
        {
            var response = await agent.RunAsync(message, session, cancellationToken: ct).ConfigureAwait(false);
            _lastTurnWebCitations = WebCitationUrls(response.Messages);

            // After the turn, not during: UseFunctionInvocation runs the whole tool loop inside
            // RunAsync, so there is no seam mid-loop. Steps therefore land in a burst when the turn
            // returns — an accepted limit, recorded in the design (§6.2).
            foreach (var call in ToolCalls(response.Messages))
                await trail.StepAsync(RunStepKind.ToolCall, Describe(call),
                    new RunStepDetail { Tool = call.Tool, Query = call.Query, ResultCount = call.ResultCount }, ct)
                    .ConfigureAwait(false);

            return response.Text;
        }

        private static string Describe(ObservedToolCall call)
        {
            var what = call.Query is { Length: > 0 } q ? $"{call.Tool} for \"{q}\"" : call.Tool;
            return call.ResultCount is { } n ? $"Called {what} — {n} result(s)." : $"Called {what}.";
        }

        /// Overrides the interface default with real incremental delivery. Uses the MAF streaming API
        /// confirmed by the Task-0 spike (see MafStreamingPathTests) — if this stops compiling after a
        /// MAF upgrade, re-run that test rather than guessing the new name.
        public async IAsyncEnumerable<string> SendStreamingAsync(
            string message, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var update in agent.RunStreamingAsync(message, session, cancellationToken: ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                var text = update.Text;
                if (!string.IsNullOrEmpty(text)) yield return text;
            }
            // Web citations are not collected here: no streaming agent has a hosted web tool, and
            // silently returning an empty set would be a lie if one ever did. If a streaming agent
            // gains one, collect from the updates and set _lastTurnWebCitations, as SendAsync does.
        }
    }
}
