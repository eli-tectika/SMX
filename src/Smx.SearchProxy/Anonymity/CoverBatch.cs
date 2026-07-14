using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Anonymity;

/// Injected so tests are deterministic and production is not.
public interface IShuffler
{
    void Shuffle<T>(IList<T> items);
}

public sealed class RandomShuffler : IShuffler
{
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// Layer 3 — the actual anonymization (spec §6.3). The real query is issued to the provider inside a
/// shuffled batch of chemically plausible decoys drawn from the same intent family, so the provider sees a
/// stream of taggant-chemistry questions spanning the catalog and cannot tell which one is the live project's.
///
/// Every query in the batch is real traffic and every result is cached (see SearchPipeline) — so the cover
/// is not waste. It warms the cache, and future real queries increasingly never egress at all.
public sealed class CoverBatch(CoverCorpus corpus, ProxyOptions opts, IShuffler shuffler)
{
    public IReadOnlyList<string> Build(string realQuery, string intent)
    {
        // opts.CoverCount is clamped to >= 2 in ProxyOptions.From; clamp again here so a hand-constructed
        // ProxyOptions in a test cannot accidentally send a naked query either.
        var size = Math.Max(2, opts.CoverCount);

        var pool = corpus.For(intent)
            .Where(q => !string.Equals(q, realQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();
        shuffler.Shuffle(pool);

        var batch = new List<string>(size) { realQuery };
        batch.AddRange(pool.Take(size - 1));
        shuffler.Shuffle(batch);
        return batch;
    }
}
