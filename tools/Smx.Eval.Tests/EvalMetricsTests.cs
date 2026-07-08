using Smx.Domain.Records;
using Smx.Eval;

namespace Smx.Eval.Tests;

public class EvalMetricsTests
{
    private static ExpectedCell E(string cas, string comp, VerdictStatus s, string track) => new(cas, comp, s, track);
    private static MatrixCell A(string cas, string comp, VerdictStatus s, bool cited = true) => new(cas, comp, s,
        [new DimensionVerdict("ElementGate", s,
            cited ? [new Citation("regulatory", "x", "t")] : [], 0.9, "r")]);

    [Fact]
    public void Agreement_IsComputedPerTrack()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "plumbing"), E("c2", "b", VerdictStatus.Pass, "reasoning")],
            [A("c1", "b", VerdictStatus.Fail), A("c2", "b", VerdictStatus.Conditional)]);
        Assert.Equal(1.0, report.Tracks["plumbing"].Agreement);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
    }

    [Fact]
    public void FalsePass_IsCountedSeparately_AsTheHarmMetric()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "reasoning"), E("c2", "b", VerdictStatus.Fail, "reasoning")],
            [A("c1", "b", VerdictStatus.Pass), A("c2", "b", VerdictStatus.Fail)]);
        Assert.Equal(1, report.FalsePassCount); // predicted clean where expected Fail
        Assert.Equal(0.5, report.Tracks["reasoning"].Agreement);
    }

    [Fact]
    public void UncitedCell_CountsAsFailure_EvenWhenVerdictAgrees()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "reasoning")],
            [A("c1", "b", VerdictStatus.Fail, cited: false)]);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
        Assert.Equal(1, report.UncitedCount);
    }

    [Fact]
    public void MissingCell_CountsAsDisagreement()
    {
        var report = EvalMetrics.Score([E("c1", "b", VerdictStatus.Fail, "reasoning")], []);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
        Assert.Equal(1, report.MissingCount);
    }
}
