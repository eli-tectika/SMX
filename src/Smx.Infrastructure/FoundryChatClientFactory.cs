using Anthropic.Foundry;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.AI;

namespace Smx.Infrastructure;

/// <summary>
/// Builds the <see cref="IChatClient"/> that MAF agents consume. Two providers, selected by
/// <see cref="BackendOptions.ModelProvider"/>:
///   • "anthropic" (default, the SOW target) — Anthropic C# SDK Foundry client
///     (<see cref="AnthropicFoundryClient"/>) → <c>AsIChatClient(deployment)</c>.
///   • "openai" — <see cref="AzureOpenAIClient"/> against the same Foundry account, a stand-in used
///     while Claude quota is unavailable. MAF agents are model-agnostic; only this construction differs.
/// Both wrap the same function-invocation pipeline.
///
/// Credential resolution (secretless-first, private-by-default):
///   (1) Entra bearer via the injected <see cref="TokenCredential"/>
///       (<see cref="AnthropicFoundryIdentityTokenCredentials"/>, default scope
///       <c>https://ai.azure.com/.default</c>) — the production path, used whenever no explicit
///       API key is configured.
///   (2) Key Vault secret <c>foundry-anthropic-key</c> (when <c>KEYVAULT_URI</c> is set).
///   (3) <c>FOUNDRY_API_KEY</c> env var (local dev only).
/// An explicit key (env, then Key Vault) overrides Entra when present; production leaves both
/// unset and lands on Entra.
/// </summary>
public static class FoundryChatClientFactory
{
    public const string KeySecretName = "foundry-anthropic-key";

    public static async Task<IChatClient> CreateAsync(BackendOptions opts, TokenCredential credential, CancellationToken ct = default)
    {
        if (opts.ModelProvider.Equals("openai", StringComparison.OrdinalIgnoreCase))
            return CreateOpenAi(opts, credential);

        if (string.IsNullOrEmpty(opts.FoundryEndpoint))
            throw new InvalidOperationException("FOUNDRY_ENDPOINT missing — required for the agent host");

        // Resolve an explicit x-api-key if one is configured: FOUNDRY_API_KEY env var first,
        // then the Key Vault secret. Absent both, we fall through to secretless Entra.
        var apiKey = opts.FoundryApiKey;
        if (apiKey is null && opts.KeyVaultUri is not null)
        {
            // The key is optional: when the secret is not present we fall through to secretless
            // Entra (the production path). Only a missing secret is tolerated — other Key Vault
            // failures (auth, network) still surface.
            try
            {
                var secrets = new SecretClient(new Uri(opts.KeyVaultUri), credential);
                apiKey = (await secrets.GetSecretAsync(KeySecretName, cancellationToken: ct)).Value.Value;
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                // secret not configured → use Entra
            }
        }

        // ResourceName is only consumed by the SDK to build a *default* base URL
        // (https://{ResourceName}.services.ai.azure.com/anthropic). We override BaseUrl below,
        // so it is informational here — derive it from the endpoint host's first label.
        var resourceName = ResourceNameFromEndpoint(opts.FoundryEndpoint);

        IAnthropicFoundryCredentials credentials = apiKey is not null
            ? new AnthropicFoundryApiKeyCredentials(apiKey, resourceName)
            : new AnthropicFoundryIdentityTokenCredentials(credential, resourceName);

        var client = new AnthropicFoundryClient(credentials) { BaseUrl = opts.AnthropicBaseUrl };

        return client.AsIChatClient(opts.ClaudeDeployment)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    /// Azure OpenAI on the Foundry AIServices account — the "openai" provider (Claude-quota stand-in).
    /// Entra-only: the workload identity needs the Cognitive Services OpenAI User role on the account.
    private static IChatClient CreateOpenAi(BackendOptions opts, TokenCredential credential)
    {
        var endpoint = opts.ResolvedOpenAiEndpoint;
        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException("OPENAI_ENDPOINT (or FOUNDRY_ENDPOINT) missing — required for the OpenAI provider");

        var azure = new AzureOpenAIClient(new Uri(endpoint), credential);
        // Responses API, not Chat Completions — deliberately. The hosted web-search tool
        // (Microsoft.Extensions.AI HostedWebSearchTool) is a Responses-only capability; a ChatClient over
        // /chat/completions silently drops it. This also aligns the code with the HLD's "Responses API only"
        // note. The response client + AsIChatClient() are marked [Experimental] in the SDK — suppressed
        // knowingly; the whole hosted path is behind WEB_SEARCH_PROVIDER=hosted and reversible.
#pragma warning disable OPENAI001
        return azure.GetResponsesClient()
            .AsIChatClient(opts.OpenAiDeployment)
#pragma warning restore OPENAI001
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    private static string ResourceNameFromEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            var label = uri.Host.Split('.', 2)[0];
            if (label.Length > 0)
                return label;
        }
        return "foundry";
    }
}
