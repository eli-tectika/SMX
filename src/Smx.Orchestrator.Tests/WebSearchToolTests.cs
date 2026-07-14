using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Tools;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

public class SensitiveTermGuardTests
{
    private static readonly SensitiveTerms Terms = new(["Acme Bottling", "HydroFizz", "proj-2026-014"]);

    [Theory]
    [InlineData("Acme Bottling marker candidates")]
    [InlineData("markers for the HYDROFIZZ bottle")]        // case-insensitive
    [InlineData("proj-2026-014 discovery")]
    public void QueriesCarryingAProjectIdentifier_AreRejected(string query)
    {
        Assert.False(SensitiveTermGuard.IsClean(query, Terms, out var offender));
        Assert.NotNull(offender);
    }

    // The whole point of a taggant search is chemistry. Rejecting ordinary chemical language would make the
    // tool useless — and a guard that fires constantly gets turned off.
    [Theory]
    [InlineData("ytterbium neodecanoate solubility in polyethylene")]
    [InlineData("rare earth taggant forms for PET bottles")]
    public void OrdinaryChemistryQueries_AreClean(string query) =>
        Assert.True(SensitiveTermGuard.IsClean(query, Terms, out _));

    // Token-boundary aware: a client called "Ion" must not blacklist the word "ionic".
    [Fact]
    public void MatchesWholeTokensOnly()
    {
        var terms = new SensitiveTerms(["Ion"]);
        Assert.True(SensitiveTermGuard.IsClean("ionic solubility of yttrium", terms, out _));
        Assert.False(SensitiveTermGuard.IsClean("markers for Ion beverages", terms, out _));
    }

    // A multi-word client name is identifying by its distinctive individual words too: a query need only
    // carry "Acme" to reveal which project "Acme Bottling Company" is being evaluated.
    [Theory]
    [InlineData("Acme marker candidates")]
    [InlineData("Bottling line taggant forms")]
    public void ADistinctiveWordOfAMultiWordClient_IsRejected(string query)
    {
        var terms = new SensitiveTerms(["Acme Bottling Company"]);
        Assert.False(SensitiveTermGuard.IsClean(query, terms, out var offender));
        Assert.NotNull(offender);
    }

    // Ubiquitous corporate words are matched only inside the full phrase, never on their own — otherwise
    // every "...Company"/"...International" client would nuke legitimate chemistry queries.
    [Theory]
    [InlineData("company-wide taggant solubility study")]
    [InlineData("international standards for taggant dosing")]
    public void AUbiquitousCorporateWord_IsNotRejectedOnItsOwn(string query)
    {
        var terms = new SensitiveTerms(["Acme Bottling Company", "Globex International"]);
        Assert.True(SensitiveTermGuard.IsClean(query, terms, out _));
    }

    // The token-boundary rule holds for per-word matching too: a client word "Ferro" must not blacklist
    // "ferrocene".
    [Fact]
    public void PerWordMatching_IsStillTokenBoundaryAware()
    {
        var terms = new SensitiveTerms(["Ferro Labs"]);
        Assert.True(SensitiveTermGuard.IsClean("ferrocene synthesis conditions", terms, out _));
    }

    // The whole point: an ordinary chemistry query with a multi-word client is still clean.
    [Fact]
    public void OrdinaryChemistryQuery_WithAMultiWordClient_IsClean()
    {
        var terms = new SensitiveTerms(["Acme Bottling Company"]);
        Assert.True(SensitiveTermGuard.IsClean("ytterbium neodecanoate solubility in polyethylene", terms, out _));
    }
}

public class WebSearchToolTests
{
    private sealed class FakeProxy(WebSearchResult result) : ISearchProxyClient
    {
        public readonly List<string> Sent = [];
        public Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct)
        {
            Sent.Add(query);
            return Task.FromResult(result);
        }
    }

    private static readonly WebSearchResult Anything =
        new([new WebHit("t", "https://example.org/a", "s", "example.org")], null);

    private static WebSearchTool Tool(FakeProxy proxy, SensitiveTerms terms, bool enabled = true, int budget = 8) =>
        new(proxy, terms, enabled, budget, NullLogger<WebSearchTool>.Instance);

    [Fact]
    public async Task CleanQuery_ReachesTheProxy()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, new SensitiveTerms(["Acme"])).SearchAsync("yttrium forms", "discovery.candidate_forms");

        Assert.Single(proxy.Sent);
        Assert.Single(result.Hits);
    }

    // Reject, do not strip. A silently-mangled query returns garbage the agent then cites; a rejection tells
    // the agent what it did wrong, and lands in the audit log where the operator can see it.
    [Fact]
    public async Task QueryWithAProjectIdentifier_NeverLeavesTheVnet()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, new SensitiveTerms(["Acme"])).SearchAsync("Acme marker forms", "discovery.candidate_forms");

        Assert.Empty(proxy.Sent);
        Assert.Empty(result.Hits);
        Assert.Contains("identifies this project", result.Note);
    }

    [Fact]
    public async Task KillSwitchOff_NeverCallsTheProxy()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, SensitiveTerms.None, enabled: false).SearchAsync("yttrium forms", "discovery.candidate_forms");

        Assert.Empty(proxy.Sent);
        Assert.Contains("disabled", result.Note);
    }

    // An agent loop must not be able to spray egress.
    [Fact]
    public async Task StageBudget_IsEnforced()
    {
        var proxy = new FakeProxy(Anything);
        var tool = Tool(proxy, SensitiveTerms.None, budget: 2);

        await tool.SearchAsync("q1", "discovery.candidate_forms");
        await tool.SearchAsync("q2", "discovery.candidate_forms");
        var third = await tool.SearchAsync("q3", "discovery.candidate_forms");

        Assert.Equal(2, proxy.Sent.Count);
        Assert.Contains("budget", third.Note);
    }
}
