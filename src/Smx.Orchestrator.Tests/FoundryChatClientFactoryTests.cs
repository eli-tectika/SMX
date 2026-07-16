using Azure.Core;
using Microsoft.Extensions.Configuration;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Tests;

/// CONSTRUCTION tests through the REAL factory — the anti-crash-loop guard.
///
/// Azure.AI.OpenAI's GetResponsesClient() binds to the OpenAI package's ResponsesClient ctor AT RUNTIME.
/// A version skew between the two packages (NuGet resolves the OpenAI floor of whichever dependent asks
/// highest) compiles clean and passes every behavioral test — no test constructed the client, so the
/// first thing to try the ctor was the orchestrator's DI at STARTUP, which crash-looped the container
/// and silently stopped the change feed (first live run, 2026-07-16: MissingMethodException on
/// 'OpenAI.Responses.ResponsesClient..ctor'). Constructing through the real factory is the only offline
/// check that catches the skew, for the same reason AIFunctionFactory schemas are tested by invoking the
/// real AIFunction: the failure lives in the binding, not in any code a mock exercises.
///
/// Construction is deliberately network-free (the credential is never asked for a token until a call is
/// made), so these run offline. If one of these ever starts needing the network, that is itself a design
/// regression worth failing on.
public class FoundryChatClientFactoryTests
{
    /// Never asked for a token during construction; throws if anything tries a live call.
    private sealed class InertCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("construction must not request a token");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("construction must not request a token");
    }

    private static BackendOptions Opts(string provider) => BackendOptions.From(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-test.documents.azure.com/",
            ["FOUNDRY_ENDPOINT"] = "https://aif-test.cognitiveservices.azure.com/",
            ["MODEL_PROVIDER"] = provider,
        }).Build());

    [Fact]
    public async Task OpenAiProvider_Constructs_TheResponsesChatClient()
    {
        // The exact path the orchestrator's DI takes when MODEL_PROVIDER=openai. A package skew fails
        // HERE (MissingMethodException from GetResponsesClient), not in production at 3am.
        var client = await FoundryChatClientFactory.CreateAsync(Opts("openai"), new InertCredential());
        Assert.NotNull(client);
    }

    [Fact]
    public async Task AnthropicProvider_Constructs_TheClaudeChatClient()
    {
        // The SOW-target path (Claude on Foundry, Entra credentials, no Key Vault configured).
        var client = await FoundryChatClientFactory.CreateAsync(Opts("anthropic"), new InertCredential());
        Assert.NotNull(client);
    }
}
