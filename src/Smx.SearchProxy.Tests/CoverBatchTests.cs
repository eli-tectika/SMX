using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class CoverBatchTests
{
    /// Deterministic "shuffle": reverses, so tests can assert on position without flaking.
    private sealed class ReverseShuffler : IShuffler
    {
        public void Shuffle<T>(IList<T> items)
        {
            var copy = items.Reverse().ToList();
            for (var i = 0; i < items.Count; i++) items[i] = copy[i];
        }
    }

    private static CoverCorpus Corpus() => CoverCorpus.FromJson(
        "{" + string.Join(",", SearchIntents.All.Select(i =>
            $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");

    private static CoverBatch Batch(int coverCount) =>
        new(Corpus(), new ProxyOptions { CoverCount = Math.Max(2, coverCount) }, new ReverseShuffler());

    private const string Real = "ytterbium neodecanoate solubility in polyethylene";

    [Fact]
    public void BatchContainsTheRealQueryExactlyOnce()
    {
        var batch = Batch(4).Build(Real, SearchIntents.CandidateForms);
        Assert.Equal(4, batch.Count);
        Assert.Single(batch, q => q == Real);
    }

    // Invariant 4: a real query never egresses alone.
    [Fact]
    public void BatchAlwaysCarriesAtLeastOneDecoy()
    {
        foreach (var n in new[] { 2, 3, 4, 8 })
        {
            var batch = Batch(n).Build(Real, SearchIntents.CandidateForms);
            Assert.True(batch.Count >= 2);
            Assert.True(batch.Count(q => q != Real) >= 1);
        }
    }

    [Fact]
    public void DecoysComeFromTheRequestedIntentFamily()
    {
        var batch = Batch(4).Build(Real, SearchIntents.SupplierAvailability);
        foreach (var decoy in batch.Where(q => q != Real))
            Assert.StartsWith(SearchIntents.SupplierAvailability, decoy);
    }

    [Fact]
    public void DecoysAreDistinctAndNeverEqualTheRealQuery()
    {
        var batch = Batch(6).Build(Real, SearchIntents.CandidateForms);
        Assert.Equal(batch.Count, batch.Distinct().Count());
    }

    // A real query pinned at index 0 in every batch would defeat the whole exercise: the observer just reads
    // the first one. It must be shuffled into the batch.
    [Fact]
    public void TheRealQueryIsNotAlwaysFirst()
    {
        var batch = Batch(4).Build(Real, SearchIntents.CandidateForms);
        Assert.NotEqual(0, batch.ToList().IndexOf(Real));
    }

    // If the real query happens to BE one of the corpus decoys, we must not send it twice — a duplicate is a
    // tell.
    [Fact]
    public void RealQueryMatchingADecoy_IsNotDuplicated()
    {
        var collision = $"{SearchIntents.CandidateForms} decoy 7";
        var batch = Batch(4).Build(collision, SearchIntents.CandidateForms);
        Assert.Equal(4, batch.Count);
        Assert.Single(batch, q => q == collision);
    }
}
