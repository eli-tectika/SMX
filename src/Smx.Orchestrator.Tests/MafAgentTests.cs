using Microsoft.Extensions.AI;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

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
}
