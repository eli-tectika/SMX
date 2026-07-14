using Microsoft.Extensions.Configuration;
using Smx.SearchProxy.Config;
using Xunit;

namespace Smx.SearchProxy.Tests;

public class ProxyOptionsTests
{
    private static ProxyOptions From(params (string Key, string Value)[] pairs) =>
        ProxyOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build());

    [Fact]
    public void Defaults_AreTheSpecDefaults()
    {
        var o = From();
        Assert.Equal("brave", o.Provider);
        Assert.Equal(4, o.CoverCount);
        Assert.Equal(256, o.MaxQueryChars);
        Assert.Equal(10, o.MaxResults);
        Assert.Equal(168, o.CacheTtlHours);
        Assert.Equal(5000, o.MonthlyQueryCap);
        Assert.False(o.DryRun);
    }

    // Invariant 4: a config value must not be able to switch the anonymization off. An invariant with an
    // off switch is not an invariant — so PROXY_COVER_COUNT is clamped, not obeyed.
    [Theory]
    [InlineData("1", 2)]
    [InlineData("0", 2)]
    [InlineData("-5", 2)]
    [InlineData("6", 6)]
    public void CoverCount_IsClampedToAtLeastTwo(string configured, int expected) =>
        Assert.Equal(expected, From(("PROXY_COVER_COUNT", configured)).CoverCount);
}
