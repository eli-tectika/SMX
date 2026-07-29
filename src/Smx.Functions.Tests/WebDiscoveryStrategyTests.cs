using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Sourcing;
using Xunit;

public sealed class FakeSearch : ISdsWebSearch
{
    private readonly Func<string, int, IReadOnlyList<WebHit>> _reply;
    public int Calls;
    public string? LastQuery;

    public FakeSearch(Func<string, int, IReadOnlyList<WebHit>> reply) => _reply = reply;
    public FakeSearch(params WebHit[] hits) : this((_, _) => hits) { }

    public Task<IReadOnlyList<WebHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        Calls++;
        LastQuery = query;
        return Task.FromResult(_reply(query, maxResults));
    }
}

public class WebDiscoveryStrategyTests
{
    private static SubstanceKey Key => new("Zr", "TMHD complex", "18865-74-2");
    private static readonly EgressFetch NoFetch =
        (_, _) => throw new InvalidOperationException("webDiscovery must not fetch");
    private static readonly AllowlistEntry Entry = WebDiscoveryStrategy.NoSupplier;

    [Fact]
    public async Task TheQueryCarriesTheCasAndTheForm()
    {
        var search = new FakeSearch();
        await new WebDiscoveryStrategy(search).ResolveAsync(Entry, Key, NoFetch, default);

        Assert.Contains("18865-74-2", search.LastQuery);
        Assert.Contains("TMHD complex", search.LastQuery);
        Assert.Contains("safety data sheet", search.LastQuery, StringComparison.OrdinalIgnoreCase);
    }

    // The property that matters is not what the query contains but what it CANNOT contain. `ensure` is
    // keyed by substance, so there is no project field on the input — and every token that leaves here
    // must be traceable either to the SubstanceKey or to a fixed SDS vocabulary.
    [Fact]
    public async Task NoTokenInTheQueryComesFromAnywhereButTheSubstance()
    {
        var search = new FakeSearch();
        await new WebDiscoveryStrategy(search).ResolveAsync(Entry, Key, NoFetch, default);

        var fromTheSubstance = new[] { "zr", "tmhd", "complex", "18865-74-2" };
        var fixedVocabulary = new[] { "safety", "data", "sheet", "sds", "filetype:pdf" };
        foreach (var token in search.LastQuery!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = token.Trim('"').ToLowerInvariant();
            Assert.True(fromTheSubstance.Contains(bare) || fixedVocabulary.Contains(bare),
                $"query token '{token}' came from neither the substance nor the fixed SDS vocabulary");
        }
    }

    [Fact]
    public async Task PdfResultsRankAheadOfPageResults()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch(
            new WebHit(new Uri("https://a.example/page"), "SDS page"),
            new WebHit(new Uri("https://b.example/sheet.pdf"), "SDS pdf")));

        var candidates = await strat.ResolveAsync(Entry, Key, NoFetch, default);

        Assert.Equal("https://b.example/sheet.pdf", candidates[0].Url.ToString());
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public async Task TheSupplierIsTheHostBecauseNobodyCuratedAName()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch(
            new WebHit(new Uri("https://chem.example/sheet.pdf"), "SDS")));

        var candidate = Assert.Single(await strat.ResolveAsync(Entry, Key, NoFetch, default));

        Assert.Equal("chem.example", candidate.Supplier);
        Assert.Equal("chem.example", candidate.Domain);
    }

    // The strategy is stamped on the candidate so a discovered hit and a curated hit stay
    // distinguishable on the registry record forever after.
    [Fact]
    public async Task TheCandidateRecordsHowItWasFound()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch(
            new WebHit(new Uri("https://chem.example/sheet.pdf"), "SDS")));

        var candidate = Assert.Single(await strat.ResolveAsync(Entry, Key, NoFetch, default));

        Assert.Equal("webDiscovery", candidate.Strategy);
        Assert.Equal("webDiscovery", strat.Name);
    }

    [Fact]
    public async Task TheCandidateListIsCapped()
    {
        var many = Enumerable.Range(0, 20)
            .Select(i => new WebHit(new Uri($"https://h{i}.example/s.pdf"), "SDS")).ToArray();
        var strat = new WebDiscoveryStrategy(new FakeSearch(many), maxCandidates: 3);

        Assert.Equal(3, (await strat.ResolveAsync(Entry, Key, NoFetch, default)).Count);
    }

    // A search that found nothing — including the outage case, which the search reports as no hits —
    // is a strategy with no candidates, not an error.
    [Fact]
    public async Task ASearchThatFindsNothingYieldsNoCandidates()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch());

        Assert.Empty(await strat.ResolveAsync(Entry, Key, NoFetch, default));
    }
}
