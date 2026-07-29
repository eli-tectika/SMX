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
        // The operator's CEILING, not the caller's page size. SearchRequest.MaxResults still defaults to 10 —
        // a caller who says nothing asks for 10; a caller may ask for up to this many.
        Assert.Equal(20, o.MaxResults);
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

    // The deployed app setting is the ENV VAR `AzureWebJobsStorage__accountName` (functions.bicep:272), and
    // .NET's environment-variable provider rewrites `__` to `:` before the key ever reaches IConfiguration.
    // A binder that asks for the literal `__` key therefore reads back nothing in Azure while every
    // in-memory test stays green — which is exactly how the host came to crash-loop on
    // "https://.blob.core.windows.net". So bind against the real provider, not a dictionary.
    [Fact]
    public void StorageAccount_BindsFromTheDeployedEnvironmentVariable()
    {
        const string key = "AzureWebJobsStorage__accountName";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "stfnspsmxdevlmxnb");
            var opts = ProxyOptions.From(new ConfigurationBuilder().AddEnvironmentVariables().Build());
            Assert.Equal("stfnspsmxdevlmxnb", opts.StorageAccount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    // local.settings.json / an in-memory config can hand the key over verbatim, without the `__` → `:`
    // rewrite. Both spellings must resolve, or fixing one form would break the other.
    [Fact]
    public void StorageAccount_AlsoBindsFromTheLiteralDoubleUnderscoreKey() =>
        Assert.Equal("stfnspexample", From(("AzureWebJobsStorage__accountName", "stfnspexample")).StorageAccount);

    [Fact]
    public void StorageAccount_DefaultsToEmptyWhenUnset() => Assert.Equal("", From().StorageAccount);

    [Fact]
    public void MaxResponseBytes_IsConfigurable() =>
        Assert.Equal(4096, From(("PROXY_MAX_RESPONSE_BYTES", "4096")).MaxResponseBytes);

    [Fact]
    public void TimeoutSeconds_IsConfigurable() =>
        Assert.Equal(9, From(("PROXY_TIMEOUT_SECONDS", "9")).TimeoutSeconds);

    // ── The dead-knob ratchet ──────────────────────────────────────────────────────────────────────────
    //
    // This proxy has now grown the same defect twice. PROXY_MAX_RESULTS was parsed into ProxyOptions while
    // StructuralGuard hardcoded `> 20`, so the operator's ceiling did nothing. PROXY_TIMEOUT_SECONDS was
    // parsed and then read by no line of code at all, so a hung provider call ran to HttpClient's 100s
    // default and the retry loop could outlive the platform's own request ceiling. Both were found by
    // reading code, months late. A knob that silently does nothing is worse than no knob: it invites
    // someone to set it and believe they have changed something.
    //
    // So the class is now closed by a ratchet rather than by vigilance. Every public option must be named
    // here together with the test that proves it reaches behaviour. Adding an option without doing so fails
    // this test, and the fix is either to write that test or to admit — in this table, in writing — that
    // nothing reads it.
    //
    // Honest about its own limit: this proves DECLARATION, not liveness. The named test is what proves
    // liveness. What the ratchet guarantees is that a new knob cannot arrive unexamined and unremarked.
    private static readonly Dictionary<string, string> KnobIsProvenLiveBy = new()
    {
        ["Provider"] = "NOTHING SELECTS ON IT — /health echoes it and BraveSearchProvider is chosen " +
                       "unconditionally. Deliberate: Invariant 1 is an allowlist of one (BraveSearchProvider.ApiHost), " +
                       "and a provider this setting could actually switch would break it.",
        ["ApiKey"] = "SearchPipelineTests.ProviderNotConfigured_Is503",
        ["DryRun"] = "HostWiringTests.DryRun_UsesTheNullStores",
        ["CoverCount"] = "CoverBatchTests.BatchAlwaysCarriesAtLeastOneDecoy",
        ["CoverCountRaw"] = "ProxyOptionsTests.CoverCount_IsClampedToAtLeastTwo (the pre-clamp value Program.cs warns on)",
        ["CoverCorpusPath"] = "HostWiringTests.TheShippedCorpusFillsTheDefaultCoverCount (loads the real artifact through it)",
        ["MaxQueryChars"] = "StructuralGuardTests.OverLongQuery_IsRejected",
        ["MaxResults"] = "StructuralGuardTests.MaxResults_IsBoundedByTheConfiguredCeiling_NotAHardcodedTwenty",
        ["TimeoutSeconds"] = "ProxyHttpTests.TheShippedClient_UsesTheConfiguredTimeout_NotHttpClientsDefault " +
                             "+ HostWiringTests.Live_AppliesTheConfiguredTimeoutToTheProvidersHttpClient",
        ["Retries"] = "BraveSearchProviderTests.ExhaustedRetries_ReturnsNullRatherThanThrowing",
        ["MaxResponseBytes"] = "BraveSearchProviderTests.ResponseOverTheConfiguredCeiling_IsRefused",
        ["CacheTtlHours"] = "CacheTests.ExpiredEntry_IsAMiss",
        ["CacheContainer"] = "UNTESTED — names the blob container in Program.cs. Reaching it needs a live " +
                             "BlobServiceClient, so it is proven by deployment, not by test.",
        ["StorageAccount"] = "ProxyOptionsTests.StorageAccount_BindsFromTheDeployedEnvironmentVariable " +
                             "+ HostWiringTests.Live_WithNoStorageAccount_FailsAtStartupNamingTheSetting",
        ["MonthlyQueryCap"] = "QuotaGuardTests.AllowsUntilTheMonthlyCap_ThenRefuses",
        ["RateLimitPerMinute"] = "QuotaGuardTests.RateLimit_RefusesABurstWithinTheSameMinute",
        ["UamiClientId"] = "UNTESTED — selects ManagedIdentityCredential over DefaultAzureCredential in " +
                           "Program.cs. Both are Azure SDK types with no observable difference offline.",
    };

    [Fact]
    public void EveryOptionDeclaresWhatProvesItLive()
    {
        var undeclared = typeof(ProxyOptions)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !KnobIsProvenLiveBy.ContainsKey(name))
            .ToList();

        Assert.True(undeclared.Count == 0,
            $"ProxyOptions gained {string.Join(", ", undeclared)} without saying what proves the setting " +
            "reaches behaviour. Add the option to KnobIsProvenLiveBy naming the test that exercises it — or, " +
            "if nothing reads it, say so there in writing. PROXY_MAX_RESULTS and PROXY_TIMEOUT_SECONDS were " +
            "both parsed-but-dead for months; this table is what stops the third one.");

        // And the table cannot outlive the options: a removed knob must lose its row too, or the table
        // decays into folklore about settings that no longer exist.
        var stale = KnobIsProvenLiveBy.Keys
            .Where(name => typeof(ProxyOptions).GetProperty(name) is null)
            .ToList();
        Assert.True(stale.Count == 0, $"KnobIsProvenLiveBy names options that no longer exist: {string.Join(", ", stale)}");
    }
}
