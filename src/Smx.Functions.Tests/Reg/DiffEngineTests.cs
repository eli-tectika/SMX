using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;
using Xunit;

namespace Smx.Functions.Tests.Reg;

public class DiffEngineTests
{
    private static DocOutcome Changed(string doc, int chunks) => new("s", doc, DocResult.Changed, chunks, null);
    private static DocOutcome Error(string doc) => new("s", doc, DocResult.Error, 0, "boom");

    [Fact]
    public void Normal_diff_below_threshold_is_not_anomalous()
    {
        var opts = new RegOptions { AnomalyDiffAbs = 1000 };
        var diff = DiffEngine.Compute("sync-202607", new[] { Changed("d1", 5), Changed("d2", 3) }, opts);

        Assert.False(diff.Anomaly.Anomalous);
        Assert.Equal(2, diff.Changed);
        Assert.Equal(new[] { "d1", "d2" }, diff.ChangedDocIds);
    }

    [Fact]
    public void Large_diff_over_absolute_threshold_trips_the_breaker()
    {
        var opts = new RegOptions { AnomalyDiffAbs = 10 };
        var diff = DiffEngine.Compute("sync-202607", new[] { Changed("d1", 50) }, opts);

        Assert.True(diff.Anomaly.Anomalous);
        Assert.Contains(diff.Anomaly.Reasons, r => r.Contains("threshold"));
    }

    [Fact]
    public void Fetch_or_parse_error_trips_the_breaker()
    {
        var opts = new RegOptions { AnomalyDiffAbs = 1000 };
        var diff = DiffEngine.Compute("sync-202607", new[] { Changed("d1", 5), Error("d2") }, opts);

        Assert.True(diff.Anomaly.Anomalous);
        Assert.Equal(1, diff.Errors);
    }

    [Fact]
    public void Changed_doc_with_zero_chunks_is_a_parse_anomaly()
    {
        var opts = new RegOptions { AnomalyDiffAbs = 1000 };
        var diff = DiffEngine.Compute("sync-202607", new[] { Changed("d1", 0) }, opts);

        Assert.True(diff.Anomaly.Anomalous);
        Assert.Contains(diff.Anomaly.Reasons, r => r.Contains("0 chunks"));
    }
}
