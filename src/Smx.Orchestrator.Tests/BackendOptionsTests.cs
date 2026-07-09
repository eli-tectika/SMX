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
        Assert.Equal(4, o.ScreeningParallelism);                        // default
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
}
