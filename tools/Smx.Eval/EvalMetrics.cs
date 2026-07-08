using Smx.Domain.Records;

namespace Smx.Eval;

public static class EvalMetrics
{
    public static EvalReport Score(IReadOnlyList<ExpectedCell> expected, IReadOnlyList<MatrixCell> actual)
    {
        var report = new EvalReport();
        var byCell = actual.ToDictionary(c => (c.Cas, c.ComponentId));
        foreach (var e in expected)
        {
            var track = report.Tracks.TryGetValue(e.Track, out var t) ? t : report.Tracks[e.Track] = new TrackScore();
            track.Total++;

            if (!byCell.TryGetValue((e.Cas, e.ComponentId), out var cell))
            {
                report.MissingCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: no cell in matrix (expected {e.Expected})");
                continue;
            }
            var uncited = cell.Dimensions.Any(d => d.Citations.Count == 0);
            if (uncited)
            {
                report.UncitedCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: uncited dimension — counts as failure");
            }
            var agreed = cell.Overall == e.Expected && !uncited;
            if (agreed) track.Agreed++;
            else if (cell.Overall != e.Expected)
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: expected {e.Expected}, got {cell.Overall}");
            // the harm metric: model said usable where the golden answer is Fail
            if (e.Expected == VerdictStatus.Fail && cell.Overall is VerdictStatus.Pass or VerdictStatus.Conditional)
                report.FalsePassCount++;
        }
        return report;
    }
}
