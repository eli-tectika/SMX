using System.Diagnostics;
using System.Net;
using System.Text;
using Anthropic.Foundry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace Smx.Orchestrator.Tests;

/// Does a MAF ChatClientAgent over our Foundry Anthropic IChatClient deliver INCREMENTAL updates, or
/// one lump at the end? The interview's entire feel rests on the answer, and nothing else in the suite
/// would notice it changing.
///
/// Everything but the socket is the production stack: the real AnthropicFoundryClient (its HttpClient
/// replaced), the real Anthropic Messages SSE wire format, the real AsIChatClient →
/// UseFunctionInvocation → ChatClientAgent chain. Like FoundryChatClientFactoryTests, this exists
/// because the failure it guards against lives in SDK BINDING — a MAF or Anthropic-SDK upgrade that
/// stopped streaming would compile clean, pass every mock-based test, and surface months later as
/// "the chat feels slow now" with nothing pointing at the cause.
///
/// Deliberately network-free. What it CANNOT see is whether the Foundry gateway buffers the upstream
/// SSE before relaying it — that is confirmed against a live deployment, not here.
public class MafStreamingPathTests(ITestOutputHelper output)
{
    /// Emits Anthropic Messages SSE one event at a time, 40 ms apart, straight into the response body.
    /// Released by the TEST once it has seen a text update. The transport waits on it before sending the
    /// LAST delta, so completing at all proves the consumer was reading while the server was still writing.
    private readonly TaskCompletionSource _sawFirstUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SseHandler(TaskCompletionSource gate) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pipe = new AnonymousPipeContent(ct, gate);
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = pipe };
            res.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(res);
        }
    }

    private sealed class AnonymousPipeContent(CancellationToken ct, TaskCompletionSource gate) : HttpContent
    {
        private static readonly string[] Words = ["Hel", "lo, ", "oper", "ator."];

        /// The default HttpContent implementation of this BUFFERS into a MemoryStream by running
        /// SerializeToStreamAsync to completion — which would fake up a "no streaming" result no matter
        /// what the SDK does. A real socket-backed response hands back a live stream, so this does too.
        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            var pipe = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteEventsAsync(pipe.Writer.AsStream());
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception e) { await pipe.Writer.CompleteAsync(e); }
            }, ct);
            return await Task.FromResult(pipe.Reader.AsStream());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteEventsAsync(stream);

        private async Task WriteEventsAsync(Stream stream)
        {
            async Task Send(string ev, string data)
            {
                var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data}\n\n");
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                await Task.Delay(40, ct);
            }

            await Send("message_start", """
                {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant",
                "model":"claude","content":[],"stop_reason":null,"stop_sequence":null,
                "usage":{"input_tokens":1,"output_tokens":1}}}
                """.ReplaceLineEndings(""));
            await Send("content_block_start",
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
            foreach (var w in Words)
            {
                if (w == Words[^1])
                    // Deadlocks (and the test times out) if the consumer only starts reading after the
                    // whole body has been buffered. Completing proves overlap.
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await Send("content_block_delta",
                    """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"""
                    + $"\"{w}\"" + "}}");
            }
            await Send("content_block_stop", """{"type":"content_block_stop","index":0}""");
            await Send("message_delta",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":4}}""");
            await Send("message_stop", """{"type":"message_stop"}""");
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    [Fact]
    public async Task RunStreamingAsync_DeliversUpdates_WhileTheResponseIsStillBeingWritten()
    {
        var client = new AnthropicFoundryClient(
            new AnthropicFoundryApiKeyCredentials("fake-key", "aif-test"))
        {
            BaseUrl = "https://aif-test.services.ai.azure.com/anthropic/v1",
            HttpClient = new HttpClient(new SseHandler(_sawFirstUpdate)),
        };

        var chatClient = client.AsIChatClient("claude-opus-4-7")
            .AsBuilder().UseFunctionInvocation().Build();

        var agent = new ChatClientAgent(chatClient,
            instructions: "You are a helpful assistant.", name: "streaming-path", tools: []);
        var session = await agent.CreateSessionAsync(cancellationToken: default);

        var sw = Stopwatch.StartNew();
        var arrivals = new List<long>();
        var text = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("say hello", session, cancellationToken: default))
        {
            arrivals.Add(sw.ElapsedMilliseconds);
            text.Append(update.Text);
            if (!string.IsNullOrEmpty(update.Text)) _sawFirstUpdate.TrySetResult();
            output.WriteLine($"{sw.ElapsedMilliseconds,6} ms  text={update.Text?.Replace("\n", "\\n")}");
        }

        output.WriteLine($"updates={arrivals.Count} first={arrivals.FirstOrDefault()} " +
                         $"last={arrivals.LastOrDefault()} joined='{text}'");
        Assert.True(arrivals.Count > 1, "no incremental updates — streaming is not available on this path");
        Assert.Equal("Hello, operator.", text.ToString());
        // If we got here at all, the transport's gate was released by a consumed update while the
        // response body was still being written — i.e. read and write genuinely overlapped.
    }
}
