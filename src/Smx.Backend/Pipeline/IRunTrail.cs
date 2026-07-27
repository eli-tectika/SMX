using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// The write side of the run trail, held by the agents.
///
/// Every string that reaches here is written BY CODE from something observed (spec D7). There is
/// deliberately no method that takes model-authored text: a step claiming a search that never happened
/// is the same class of harm as a fabricated verdict, and the way to make that impossible is to give
/// the model no way to write one.
public interface IRunTrail
{
    Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default);
    Task CompleteAsync(string outcome, string? error, CancellationToken ct);
}

/// For tests and for paths that legitimately have no run (a converse turn before A2 wires one).
public sealed class NullRunTrail : IRunTrail
{
    public static readonly NullRunTrail Instance = new();
    public Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task CompleteAsync(string outcome, string? error, CancellationToken ct) => Task.CompletedTask;
}
