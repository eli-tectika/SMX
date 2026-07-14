using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class StructuralGuardTests
{
    private static readonly StructuralGuard Guard = new(new ProxyOptions());

    private static SearchRequest Req(string query, string intent = SearchIntents.CandidateForms, int max = 10) =>
        new(query, intent, max);

    [Fact]
    public void CleanChemistryQuery_IsAllowed()
    {
        var v = Guard.Check(Req("ytterbium neodecanoate solubility in polyethylene"));
        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
    }

    // A CAS number must survive the digit-run rule — hyphens break the run, which is why the rule is
    // \d{7,} and not "contains many digits". Rejecting CAS numbers would make the tool useless.
    [Fact]
    public void CasNumber_IsAllowed()
    {
        Assert.True(Guard.Check(Req("CAS 1314-36-9 yttrium oxide XRF")).Allowed);
    }

    [Theory]
    [InlineData("marker for 3f2504e0-4f89-11d3-9a0c-0305e82c3301", "contains_guid")]
    [InlineData("ask eli@tectika.com about the marker", "contains_email")]
    [InlineData("see https://internal.smx/projects/42", "contains_url")]
    [InlineData("visit www.acme-bottling.com marker", "contains_url")]
    [InlineData("purchase order 100045567788 marker", "contains_digit_run")]
    public void IdentifierShapedQueries_AreRejected(string query, string expectedReason)
    {
        var v = Guard.Check(Req(query));
        Assert.False(v.Allowed);
        Assert.Equal(expectedReason, v.Reason);
    }

    [Fact]
    public void EmptyQuery_IsRejected() =>
        Assert.Equal("query_empty", Guard.Check(Req("   ")).Reason);

    [Fact]
    public void OverLongQuery_IsRejected() =>
        Assert.Equal("query_too_long", Guard.Check(Req(new string('a', 257))).Reason);

    [Fact]
    public void UnknownIntent_IsRejected() =>
        Assert.Equal("unknown_intent", Guard.Check(Req("yttrium forms", intent: "regulatory.screen")).Reason);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void MaxResultsOutOfRange_IsRejected(int max) =>
        Assert.Equal("max_results_out_of_range", Guard.Check(Req("yttrium forms", max: max)).Reason);

    /// PROXY_MAX_RESULTS is the OPERATOR'S CEILING, and this is the test that makes it real. The guard used to
    /// hardcode `> 20`, so ProxyOptions.MaxResults was read by nothing: an operator who set PROXY_MAX_RESULTS=5
    /// got 20 anyway and no error anywhere. A config knob that silently does nothing is worse than no knob —
    /// it invites someone to rely on it. (Not to be confused with SearchRequest.MaxResults' own default of 10:
    /// that is the caller's default PAGE SIZE, a different thing from the operator's ceiling.)
    [Fact]
    public void MaxResults_IsBoundedByTheConfiguredCeiling_NotAHardcodedTwenty()
    {
        var tight = new StructuralGuard(new ProxyOptions { MaxResults = 5 });
        Assert.True(tight.Check(Req("yttrium forms", max: 5)).Allowed);
        Assert.Equal("max_results_out_of_range", tight.Check(Req("yttrium forms", max: 10)).Reason);

        // The shipped default must leave behaviour exactly where it was: 20 in, 21 out.
        Assert.True(Guard.Check(Req("yttrium forms", max: 20)).Allowed);
        Assert.Equal("max_results_out_of_range", Guard.Check(Req("yttrium forms", max: 21)).Reason);
    }
}
