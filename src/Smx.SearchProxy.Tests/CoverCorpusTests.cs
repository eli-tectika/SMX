using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Contracts;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class CoverCorpusTests
{
    private static string Json(int perFamily) =>
        "{" + string.Join(",", SearchIntents.All.Select(i =>
            $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, perFamily).Select(n => $"\"decoy {i} {n}\"")) + "]")) + "}";

    [Fact]
    public void LoadsEveryIntentFamily()
    {
        var corpus = CoverCorpus.FromJson(Json(20));
        foreach (var intent in SearchIntents.All)
            Assert.Equal(20, corpus.For(intent).Count);
    }

    // A new intent must not be able to ship without its decoys: it would egress a real query naked, inside a
    // batch the proxy could not fill. Fail at startup, loudly, not at 3am on the first live query.
    [Fact]
    public void ThrowsWhenAnIntentHasNoFamily()
    {
        var missing = "{\"discovery.candidate_forms\":[\"a\",\"b\"]}";
        var ex = Assert.Throws<InvalidOperationException>(() => CoverCorpus.FromJson(missing));
        Assert.Contains("discovery.form_properties", ex.Message);
    }

    [Fact]
    public void ThrowsWhenAFamilyIsTooThinToHideAQuery()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CoverCorpus.FromJson(Json(3)));
        Assert.Contains("at least 20", ex.Message);
    }

    [Fact]
    public void TheShippedCorpusIsValid()
    {
        // The real artifact, loaded exactly as production loads it. If the generator regressed, this fails.
        var corpus = CoverCorpus.FromFile("Config/cover-corpus.json");
        foreach (var intent in SearchIntents.All)
            Assert.InRange(corpus.For(intent).Count, 20, int.MaxValue);
    }
}
