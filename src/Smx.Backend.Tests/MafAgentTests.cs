using Microsoft.Extensions.AI;
using Smx.Backend.Agents;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

// MafAgent.WebCitationUrls is the code-observed input to Discovery's deterministic RAIL-1 re-stamp: it reads
// the URLs a hosted web-search tool actually returned from the response's CitationAnnotations. Driven here
// against hand-built messages (the method takes ChatMessages, not the hard-to-construct AgentResponse) so the
// extraction is pinned independently of a live model call.
public class MafAgentTests
{
    private static ChatMessage Assistant(params AIAnnotation[] annotations)
    {
        var text = new TextContent("…answer…") { Annotations = [.. annotations] };
        return new ChatMessage(ChatRole.Assistant, new List<AIContent> { text });
    }

    [Fact]
    public void WebCitationUrls_CollectsEveryCitationAnnotationUrl()
    {
        var messages = new[]
        {
            Assistant(
                new CitationAnnotation { Url = new Uri("https://pubchem.ncbi.nlm.nih.gov/compound/1") },
                new CitationAnnotation { Url = new Uri("https://echa.europa.eu/substance/2") }),
        };

        var urls = MafAgent.WebCitationUrls(messages);

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://pubchem.ncbi.nlm.nih.gov/compound/1", urls);
        Assert.Contains("https://echa.europa.eu/substance/2", urls);
    }

    [Fact]
    public void WebCitationUrls_IgnoresCitationsWithNoUrl_AndPlainText()
    {
        var messages = new[]
        {
            Assistant(new CitationAnnotation { Title = "a citation carrying no Url" }),
            new ChatMessage(ChatRole.Assistant, "plain text, no annotations"),
        };

        Assert.Empty(MafAgent.WebCitationUrls(messages));
    }

    // The no-web-tool case — every agent except a hosted Discovery run — must be empty and cheap.
    [Fact]
    public void WebCitationUrls_OnAResponseWithNoWebTool_IsEmpty()
    {
        var messages = new[] { new ChatMessage(ChatRole.Assistant, "{ \"substances\": [] }") };
        Assert.Empty(MafAgent.WebCitationUrls(messages));
    }

    // The interview's entire feel rests on chunks actually arriving incrementally rather than as one lump
    // relabeled as "streaming" — see MafStreamingPathTests for the live-wire proof; this pins the adapter.
    [Fact]
    public async Task SendStreamingAsync_YieldsChunks_AndTheConcatenationIsTheWholeReply()
    {
        var client = new FakeChatClient(streamingChunks: ["Hel", "lo, ", "operator."]);
        var agent = new MafAgent(client, "interview", "instructions", []);
        var thread = await agent.StartThreadAsync(default);

        var chunks = new List<string>();
        await foreach (var chunk in thread.SendStreamingAsync("hi", default))
            chunks.Add(chunk);

        // Both halves matter: the caller streams the chunks to the browser AND persists the join.
        Assert.True(chunks.Count > 1, "no incremental chunks — the operator watches a spinner");
        Assert.Equal("Hello, operator.", string.Concat(chunks));
    }

    // The interface default exists so no stage agent must change. It is correct-but-unhelpful
    // rather than unimplemented: a caller written against this interface works on every agent.
    private sealed class SendAsyncOnlyThread : ISmxAgentThread
    {
        public Task<string> SendAsync(string message, CancellationToken ct) => Task.FromResult("whole reply, no streaming");
    }

    [Fact]
    public async Task SendStreamingAsync_DefaultsToOneChunk_ForAThreadThatOnlyImplementsSendAsync()
    {
        ISmxAgentThread thread = new SendAsyncOnlyThread();

        var chunks = new List<string>();
        await foreach (var chunk in thread.SendStreamingAsync("hi", default))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("whole reply, no streaming", chunks[0]);
    }

    /// The tool call is read from the SDK's own record of what it invoked — not from anything the
    /// model wrote about itself.
    [Fact]
    public void Tool_calls_are_read_from_the_response_messages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "search_reference",
                new Dictionary<string, object?> { ["query"] = "zirconium oxide in PET" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "[{},{},{}]")]),
        };

        var calls = MafAgent.ToolCalls(messages).ToList();

        Assert.Single(calls);
        Assert.Equal("search_reference", calls[0].Tool);
        Assert.Equal("zirconium oxide in PET", calls[0].Query);
        Assert.Equal(3, calls[0].ResultCount);
    }

    /// A result that is not a JSON array has no countable hits. Reporting one would put a number in an
    /// audit trail that nobody measured.
    [Fact]
    public void A_tool_result_that_is_not_a_json_array_carries_no_count()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_catalog", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "not json")]),
        };

        var call = Assert.Single(MafAgent.ToolCalls(messages));
        Assert.Null(call.Query);
        Assert.Null(call.ResultCount);
    }

    /// A call whose result never came back is still a call the agent made. Dropping it would make a
    /// timed-out tool look like a tool that was never reached.
    [Fact]
    public void A_call_with_no_matching_result_is_still_reported()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "search_regulatory",
                new Dictionary<string, object?> { ["query"] = "REACH annex XVII lead" })]),
        };

        var call = Assert.Single(MafAgent.ToolCalls(messages));
        Assert.Equal("search_regulatory", call.Tool);
        Assert.Equal("REACH annex XVII lead", call.Query);
        Assert.Null(call.ResultCount);
    }
}
