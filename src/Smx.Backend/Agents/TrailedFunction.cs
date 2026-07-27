using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Agents;

/// A tool that writes its own step to the run trail AS IT COMPLETES, rather than after the turn.
///
/// This exists because of what the operator sees. `UseFunctionInvocation` runs the entire tool loop
/// inside one `RunAsync`, so harvesting `FunctionCallContent` from the finished response — the
/// previous approach — delivered every step in a single burst when the turn returned. Measured on a
/// live discovery run: one `started` step at 20:28:47, nothing for 1m45s, then twenty-six steps all
/// stamped 20:30:32. The trail was accurate and useless: an operator watching a three-minute stage
/// saw one line and then a wall.
///
/// Wrapping the tool is the seam the loop does not otherwise offer. The step is written AFTER the
/// inner call returns, not before, so the same step can carry both the query it was given and the
/// number of results it actually produced — a "searching…" line that never gains its count would be
/// two rows per call and no more information.
///
/// Everything written here is still code-observed (spec D7): the arguments come from the SDK's own
/// invocation and the count from the real result. The model gets no say in what this says.
internal sealed class TrailedFunction(AIFunction inner, IRunTrail trail, SemaphoreSlim writeLock)
    : AIFunction
{
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override JsonElement JsonSchema => inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;
    public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

    /// Wrap a tool list. The lock is SHARED across the whole list on purpose — see InvokeCoreAsync.
    internal static IList<AITool> Wrap(IList<AITool> tools, IRunTrail trail)
    {
        if (ReferenceEquals(trail, NullRunTrail.Instance)) return tools;
        var writeLock = new SemaphoreSlim(1, 1);
        return [.. tools.Select(t => t is AIFunction f ? new TrailedFunction(f, trail, writeLock) : t)];
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // InvokeAsync, not InvokeCoreAsync: the core method is protected, and the public entry point
        // is the correct seam for a decorator anyway — it keeps whatever the inner function does
        // around its own invocation.
        var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

        // RunTrail is single-writer BY ASSUMPTION and mutates its step list without a lock (see the
        // note on the class). FunctionInvokingChatClient today invokes sequentially —
        // AllowConcurrentInvocation defaults to false — so this lock is not currently load-bearing.
        // It is here so that flipping that flag, or a library default changing under us, cannot
        // silently corrupt a trail: the failure would be interleaved writes to a List<RunStep>,
        // which is exactly the kind of bug that shows up as a missing step months later.
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = FirstStringArgument(arguments);
            await trail.StepAsync(
                RunStepKind.ToolCall,
                Describe(Name, query, ResultCount(result)),
                new RunStepDetail { Tool = Name, Query = query, ResultCount = ResultCount(result) },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }

        return result;
    }

    /// The first STRING argument, which is the query on every tool in ToolBox that takes one. Not
    /// looked up by name ("query") because the parameter names differ across tools, and not the whole
    /// argument bag because this is a display sentence, not a payload dump.
    private static string? FirstStringArgument(AIFunctionArguments arguments) =>
        arguments.Values.OfType<string>().FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    internal static string Describe(string tool, string? query, int? resultCount)
    {
        var what = query is { Length: > 0 } q ? $"{tool} for \"{Trim(q)}\"" : tool;
        return resultCount is { } n ? $"Called {what} — {n} result(s)." : $"Called {what}.";
    }

    /// A long query is a wall in a timeline row. The full text still reaches `detail.query`.
    private static string Trim(string q) => q.Length <= 80 ? q : q[..77] + "…";

    /// A hit count when the result is a JSON array or a collection; null otherwise. NEVER a guess —
    /// "6 hits" that was inferred rather than counted is a fabricated number in an audit trail.
    internal static int? ResultCount(object? result) => result switch
    {
        null => null,
        string s => CountJsonArray(s),
        System.Collections.ICollection c => c.Count,
        JsonElement { ValueKind: JsonValueKind.Array } e => e.GetArrayLength(),
        // The tools return their payload as JSON text, so anything else is a shape this does not
        // recognise. Saying nothing beats inventing a number.
        _ => CountJsonArray(result.ToString()),
    };

    private static int? CountJsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
        }
        catch (JsonException) { return null; }
    }
}
