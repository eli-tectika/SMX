using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

/// Tool steps must reach the operator DURING the turn.
///
/// The behaviour these pin is a UX fact, not an implementation detail: harvesting tool calls from the
/// finished response delivered a three-minute stage as one line followed by a wall of twenty-six
/// steps, all stamped the same second. A step written as its tool completes is the difference between
/// a screen that is working and a screen that looks hung.
public class TrailedFunctionTests
{
    private sealed class RecordingTrail : IRunTrail
    {
        public List<(string Kind, string Text, RunStepDetail? Detail)> Steps { get; } = [];
        public Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default)
        {
            Steps.Add((kind, text, detail));
            return Task.CompletedTask;
        }
        public Task CompleteAsync(string outcome, string? error, CancellationToken ct) => Task.CompletedTask;
    }

    private static AIFunction Tool(string name, Func<string, string> body) =>
        AIFunctionFactory.Create((string query) => body(query), name);

    /// The load-bearing one. The step must exist BEFORE the caller has finished invoking every tool —
    /// which is what "the operator sees progress" actually means.
    [Fact]
    public async Task Writes_its_step_as_the_tool_completes_not_after_the_batch()
    {
        var trail = new RecordingTrail();
        var wrapped = TrailedFunction.Wrap([Tool("search_reference", _ => "[{},{}]")], trail);
        var fn = Assert.IsAssignableFrom<AIFunction>(wrapped[0]);

        await fn.InvokeAsync(new AIFunctionArguments { ["query"] = "zirconium oxide in PET" });
        Assert.Single(trail.Steps);

        await fn.InvokeAsync(new AIFunctionArguments { ["query"] = "yttrium in paper" });
        Assert.Equal(2, trail.Steps.Count);
    }

    [Fact]
    public async Task Carries_the_query_and_the_counted_results()
    {
        var trail = new RecordingTrail();
        var wrapped = TrailedFunction.Wrap([Tool("search_reference", _ => "[{},{},{}]")], trail);

        await ((AIFunction)wrapped[0]).InvokeAsync(new AIFunctionArguments { ["query"] = "zirconium oxide in PET" });

        var (kind, text, detail) = Assert.Single(trail.Steps);
        Assert.Equal(RunStepKind.ToolCall, kind);
        Assert.Equal("Called search_reference for \"zirconium oxide in PET\" — 3 result(s).", text);
        Assert.Equal("search_reference", detail!.Tool);
        Assert.Equal("zirconium oxide in PET", detail.Query);
        Assert.Equal(3, detail.ResultCount);
    }

    /// The two shapes that actually reach this code in production, both of which the first version
    /// got wrong and reported as "no query, no count" on every single row.
    [Fact]
    public async Task Reads_a_query_the_SDK_handed_over_as_a_JsonElement()
    {
        var trail = new RecordingTrail();
        var wrapped = (AIFunction)TrailedFunction.Wrap([Tool("search_catalog", _ => "[]")], trail)[0];

        // The SDK deserializes arguments before invoking, so they arrive as JsonElement rather than
        // string. A plain OfType<string>() matches nothing here.
        using var doc = JsonDocument.Parse("\"zirconium\"");
        await wrapped.InvokeAsync(new AIFunctionArguments { ["query"] = doc.RootElement.Clone() });

        Assert.Equal("zirconium", Assert.Single(trail.Steps).Detail!.Query);
    }

    /// Every searching tool in ToolBox returns `{ "results": [...] }`, not a bare array.
    [Fact]
    public async Task Counts_the_results_envelope_the_tools_actually_return()
    {
        var trail = new RecordingTrail();
        var wrapped = (AIFunction)TrailedFunction.Wrap(
            [Tool("search_catalog", _ => "{\"results\":[{},{},{},{}]}")], trail)[0];

        await wrapped.InvokeAsync(new AIFunctionArguments { ["query"] = "Zr" });

        Assert.Equal(4, Assert.Single(trail.Steps).Detail!.ResultCount);
    }

    /// A count that was inferred rather than counted is a fabricated number in an audit trail.
    [Fact]
    public async Task Reports_no_count_when_the_result_is_not_a_countable_shape()
    {
        var trail = new RecordingTrail();
        var wrapped = TrailedFunction.Wrap([Tool("lookup_compatibility", _ => "{\"tabulated\":true}")], trail);

        await ((AIFunction)wrapped[0]).InvokeAsync(new AIFunctionArguments { ["query"] = "Zr in PET" });

        var (_, text, detail) = Assert.Single(trail.Steps);
        Assert.Null(detail!.ResultCount);
        Assert.DoesNotContain("result(s)", text);
    }

    /// The wrapper must not change what the model sees, or it would change what the agent does.
    [Fact]
    public async Task Passes_the_result_through_untouched_and_keeps_the_tool_identity()
    {
        var trail = new RecordingTrail();
        var inner = Tool("search_catalog", q => $"[\"{q}\"]");
        var wrapped = (AIFunction)TrailedFunction.Wrap([inner], trail)[0];

        Assert.Equal(inner.Name, wrapped.Name);
        Assert.Equal(inner.Description, wrapped.Description);
        Assert.Equal(inner.JsonSchema.ToString(), wrapped.JsonSchema.ToString());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { ["query"] = "Zr" });
        Assert.Contains("Zr", result?.ToString());
    }

    /// A run with no trail is a converse turn or a test. It must not pay for a wrapper, and it must
    /// certainly not get a different tool list than the one it passed in.
    [Fact]
    public void Leaves_the_tools_alone_when_there_is_no_trail()
    {
        IList<AITool> tools = [Tool("search_reference", _ => "[]")];
        Assert.Same(tools, TrailedFunction.Wrap(tools, NullRunTrail.Instance));
    }
}
