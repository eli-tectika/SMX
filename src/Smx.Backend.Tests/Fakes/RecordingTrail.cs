using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests.Fakes;

/// An <see cref="IRunTrail"/> that keeps what was written instead of persisting it, so a test can assert
/// on the SENTENCES an operator would read rather than on the fact that some method was called.
public sealed class RecordingTrail : IRunTrail
{
    public List<RunStep> Steps { get; } = [];
    public string? Outcome { get; private set; }
    public string? Error { get; private set; }

    public Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default)
    {
        Steps.Add(new RunStep { Seq = Steps.Count + 1, Kind = kind, Text = text, Detail = detail });
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string outcome, string? error, CancellationToken ct)
    {
        Outcome = outcome;
        Error = error;
        return Task.CompletedTask;
    }
}
