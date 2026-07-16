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

    // ---- the design-§9 Dosing invariants (ScoreDosing) --------------------------------------------------

    private static PpmWindow Window(string cas, string element, double recommended) => new("bottle", cas, element,
        Floor: new Bound(11.0, "measured", BoundKinds.Measured, 1.0),
        Upper: new Bound(1100.0, "estimate", BoundKinds.Estimate, 0.5),
        RecommendedPpm: recommended, QuantificationPpm: 35.0);

    private static CodeMarker Marker(string cas, string element) => new(cas, element, 110.0, 0.74, 1.0, 2.0);

    private static DosingDoc Dosing(List<PpmWindow> windows, params MarkerCode[] codes) => new()
    {
        Id = "p|dosing", ProjectId = "p", Windows = windows, Codes = [.. codes], GeneratedAt = "t",
    };

    [Fact]
    public void ScoreDosing_OnACleanDoc_AddsNoFalsePasses()
    {
        var report = new EvalReport();
        EvalMetrics.ScoreDosing(Dosing(
            [Window("cas-zr", "Zr", 110.0), Window("cas-y", "Y", 85.0)],
            new MarkerCode("bottle", [Marker("cas-zr", "Zr"), Marker("cas-y", "Y")], "ratio")), report);
        Assert.Equal(0, report.FalsePassCount);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void ScoreDosing_ABelowFloorWindow_IsAFalsePass()
    {
        // The headline harm: a recommended ppm at or below the floor is a marker the deployment device
        // cannot read, and nothing downstream re-checks it. It must trip the harness's non-zero exit.
        var report = new EvalReport();
        EvalMetrics.ScoreDosing(Dosing(
            [Window("cas-zr", "Zr", 5.0), Window("cas-y", "Y", 85.0)],   // cas-zr dosed below its 11.0 floor
            new MarkerCode("bottle", [Marker("cas-zr", "Zr"), Marker("cas-y", "Y")], "ratio")), report);
        Assert.Equal(1, report.FalsePassCount);
        Assert.Contains(report.Failures, f => f.Contains("cas-zr") && f.Contains("window"));
    }

    [Fact]
    public void ScoreDosing_AOneMarkerCode_IsAFalsePass()
    {
        // One marker has no ratio — it is not a code, and a field reader cannot match it.
        var report = new EvalReport();
        EvalMetrics.ScoreDosing(Dosing(
            [Window("cas-zr", "Zr", 110.0)],
            new MarkerCode("bottle", [Marker("cas-zr", "Zr")], "ratio")), report);
        Assert.Equal(1, report.FalsePassCount);
        Assert.Contains(report.Failures, f => f.Contains("2–3 markers"));
    }

    [Fact]
    public void ScoreDosing_AMarkerWithNoWindow_IsAFalsePass()
    {
        // Windows are built only over the compliant set, so a marker CAS with no window is a marker OUTSIDE
        // the compliant set — a substance the operator never recommended, inside a shipping code.
        var report = new EvalReport();
        EvalMetrics.ScoreDosing(Dosing(
            [Window("cas-zr", "Zr", 110.0), Window("cas-y", "Y", 85.0)],
            new MarkerCode("bottle",
                [Marker("cas-zr", "Zr"), Marker("cas-y", "Y"), Marker("cas-no", "Ba")], "ratio")), report);
        Assert.Equal(1, report.FalsePassCount);
        Assert.Contains(report.Failures, f => f.Contains("cas-no") && f.Contains("no ppm window"));
    }
}
