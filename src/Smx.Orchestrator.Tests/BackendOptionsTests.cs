using Microsoft.Extensions.Configuration;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Tests;

public class BackendOptionsTests
{
    [Fact]
    public void From_ReadsEnvironmentShapedConfig_WithDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FOUNDRY_ENDPOINT"] = "https://aif-smx-dev.services.ai.azure.com",
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-smx-dev.documents.azure.com:443/",
            ["SEARCH_ENDPOINT"] = "https://srch-smx-dev.search.windows.net",
            ["UAMI_CLIENT_ID"] = "client-id",
        }).Build();

        var o = BackendOptions.From(config);
        Assert.Equal("claude-opus-4-7", o.ClaudeDeployment);           // default
        Assert.Equal("smx", o.CosmosDatabase);                          // default
        Assert.Equal("record", o.RecordContainer);                      // default
        Assert.Equal("sds-index", o.SdsIndex);                          // default
        Assert.Equal("smx-reference", o.ReferenceIndex);                // default
        Assert.Equal("regulatory-index", o.RegulatoryIndex);            // default; overridden when team schema lands
        Assert.Equal("ref-compatibility", o.CompatibilityContainer);    // default
        Assert.Equal(4, o.RegulatoryParallelism);                       // default
        Assert.Equal("https://aif-smx-dev.services.ai.azure.com/anthropic/v1", o.AnthropicBaseUrl);
        Assert.Equal("anthropic", o.ModelProvider);                     // default — SOW target
        Assert.Equal("gpt-5-mini", o.OpenAiDeployment);                 // default stand-in deployment
        Assert.Equal("https://aif-smx-dev.services.ai.azure.com", o.ResolvedOpenAiEndpoint); // falls back to Foundry endpoint
    }

    [Fact]
    public void From_HonorsOpenAiProviderOverrides()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-smx-dev.documents.azure.com:443/",
            ["MODEL_PROVIDER"] = "openai",
            ["OPENAI_DEPLOYMENT"] = "gpt-5-mini",
            ["OPENAI_ENDPOINT"] = "https://aif-smx-dev-lmxnb.openai.azure.com/",
        }).Build();

        var o = BackendOptions.From(config);
        Assert.Equal("openai", o.ModelProvider);
        Assert.Equal("https://aif-smx-dev-lmxnb.openai.azure.com/", o.ResolvedOpenAiEndpoint); // explicit wins over fallback
    }

    /// The kill switch defaults ON, but the endpoint defaults EMPTY — and Program.cs ands the two together.
    /// A deployment that has not been given a proxy therefore never searches the web (it falls back to the
    /// catalog) instead of failing to start or, far worse, egressing to somewhere unconfigured.
    [Fact]
    public void From_WebSearchDefaults_EnabledButWithNoProxyToReach()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-smx-dev.documents.azure.com:443/",
        }).Build();

        var o = BackendOptions.From(config);
        Assert.True(o.WebSearchEnabled);            // the switch is on...
        Assert.Equal("", o.SearchProxyEndpoint);    // ...but there is nothing to call
        Assert.Equal("", o.SearchProxyAudience);
        Assert.Equal(8, o.WebSearchMaxPerStage);    // default per-stage budget
    }

    [Fact]
    public void From_HonorsTheWebSearchKillSwitchAndBudget()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-smx-dev.documents.azure.com:443/",
            ["SEARCH_PROXY_ENDPOINT"] = "https://func-smx-searchproxy-dev.azurewebsites.net",
            ["SEARCH_PROXY_AUDIENCE"] = "api://smx-search-proxy",
            ["WEB_SEARCH_ENABLED"] = "false",
            ["WEB_SEARCH_MAX_PER_STAGE"] = "3",
        }).Build();

        var o = BackendOptions.From(config);
        Assert.False(o.WebSearchEnabled);
        Assert.Equal("https://func-smx-searchproxy-dev.azurewebsites.net", o.SearchProxyEndpoint);
        Assert.Equal("api://smx-search-proxy", o.SearchProxyAudience);
        Assert.Equal(3, o.WebSearchMaxPerStage);
    }
}
