using Microsoft.Extensions.Configuration;

namespace Smx.Infrastructure;

public sealed record BackendOptions(
    string FoundryEndpoint,
    string ClaudeDeployment,
    string EmbeddingDeployment,
    string CosmosAccountEndpoint,
    string CosmosDatabase,
    string RecordContainer,
    string LeaseContainer,
    string CompatibilityContainer,
    string CatalogContainer,
    string LearnedConclusionsContainer,
    string MarkerLibraryContainer,
    string MsdsRegistryContainer,
    string SubstancePropertiesContainer,
    // The SDS library subsystem's corpus registry (read-only here): the MSDS Registry surface
    // composes sheet facts from it at read time (design §6.3 — reference, don't duplicate).
    string SdsRegistryContainer,
    string SearchEndpoint,
    string SdsIndex,
    string ReferenceIndex,
    string RegulatoryIndex,
    string LearnedConclusionsIndex,
    string? UamiClientId,
    string? FoundryApiKey,           // local-dev only; production resolves Entra first, then Key Vault
    string? KeyVaultUri,
    int RegulatoryParallelism,
    // Chat provider selection. "anthropic" (default) → Claude on Foundry (the SOW target);
    // "openai" → Azure OpenAI on the same Foundry account, used as a stand-in when Claude quota
    // is unavailable. MAF agents are model-agnostic, so only the IChatClient construction differs.
    string ModelProvider,
    string OpenAiDeployment,
    string OpenAiEndpoint,
    // The anonymizing Search Proxy — Discovery's ONLY route to the public internet.
    string SearchProxyEndpoint,
    string SearchProxyAudience,
    bool WebSearchEnabled,
    int WebSearchMaxPerStage,
    // Discovery's web-search backend — two supported modes, switchable by config alone. "hosted" (default) =
    // the model's built-in, provider-executed web search (Microsoft.Extensions.AI HostedWebSearchTool over the
    // Responses API). "proxy" = the anonymizing Search Proxy egress (see WebSearchTool / SearchProxyClient):
    // slower and narrower, but it is the mode that buys k-anonymity cover queries and a project-blind
    // contract — switch to it when those guarantees are wanted. The hosted tool is a Responses-API capability,
    // so it is only meaningful on the model paths that expose it.
    string WebSearchProvider)
{
    /// The pre-project interview scratchpad's container. Separate from RecordContainer on purpose —
    /// see IntakeSessionDoc's class comment.
    public string IntakeSessionContainer { get; init; } = "intake-sessions";

    public string AnthropicBaseUrl => $"{FoundryEndpoint.TrimEnd('/')}/anthropic/v1";

    /// True when Discovery should use the model's built-in hosted web search rather than the legacy proxy.
    public bool UseHostedWebSearch => WebSearchProvider.Equals("hosted", StringComparison.OrdinalIgnoreCase);

    /// Azure OpenAI endpoint for the "openai" provider. Falls back to the Foundry account endpoint
    /// (an AIServices account serves OpenAI there too) when OPENAI_ENDPOINT is not set.
    public string ResolvedOpenAiEndpoint => string.IsNullOrEmpty(OpenAiEndpoint) ? FoundryEndpoint : OpenAiEndpoint;

    // FOUNDRY_ENDPOINT / SEARCH_ENDPOINT default to "" (the API host doesn't use them);
    // the components that actually need them throw — see FoundryChatClientFactory and the
    // orchestrator Program.cs guard in Task 13.
    public static BackendOptions From(IConfiguration c) => new(
        FoundryEndpoint: c["FOUNDRY_ENDPOINT"] ?? "",
        ClaudeDeployment: c["CLAUDE_DEPLOYMENT"] ?? "claude-opus-4-7",
        // infra/modules/ai.bicep deploys text-embedding-3-large unconditionally under exactly this name,
        // so an unset EMBEDDING_DEPLOYMENT lands on the deployment that actually exists — the default is
        // correct, not merely tolerated. Must stay the model the index's 3072-dim vector field was sized for.
        EmbeddingDeployment: c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
        CosmosAccountEndpoint: c["COSMOS_ACCOUNT_ENDPOINT"] ?? throw new InvalidOperationException("COSMOS_ACCOUNT_ENDPOINT missing"),
        CosmosDatabase: c["COSMOS_DATABASE"] ?? "smx",
        RecordContainer: c["RECORD_CONTAINER"] ?? "record",
        LeaseContainer: c["RECORD_LEASE_CONTAINER"] ?? "record-leases",
        CompatibilityContainer: c["COMPATIBILITY_CONTAINER"] ?? "ref-compatibility",
        CatalogContainer: c["CATALOG_CONTAINER"] ?? "ref-catalog",
        LearnedConclusionsContainer: c["LEARNED_CONCLUSIONS_CONTAINER"] ?? "learned-conclusions",
        MarkerLibraryContainer: c["MARKER_LIBRARY_CONTAINER"] ?? "marker-library",
        MsdsRegistryContainer: c["MSDS_REGISTRY_CONTAINER"] ?? "msds-registry",
        SubstancePropertiesContainer: c["SUBSTANCE_PROPERTIES_CONTAINER"] ?? "substance-properties",
        SdsRegistryContainer: c["SDS_REGISTRY_CONTAINER"] ?? "sds-registry",
        SearchEndpoint: c["SEARCH_ENDPOINT"] ?? "",
        SdsIndex: c["SDS_SEARCH_INDEX"] ?? "sds-index",
        ReferenceIndex: c["REFERENCE_SEARCH_INDEX"] ?? "smx-reference",
        // regulatory-corpus is the name the reg-sync pipeline actually creates (RegOptions.SearchIndex).
        // The old 'regulatory-index' default predated the estate and only ever worked because both bicep
        // twins set the env var explicitly — leaving local dev pointed at an index that does not exist.
        RegulatoryIndex: c["REGULATORY_SEARCH_INDEX"] ?? "regulatory-corpus",
        LearnedConclusionsIndex: c["LEARNED_CONCLUSIONS_SEARCH_INDEX"] ?? "learned-conclusions",
        UamiClientId: c["UAMI_CLIENT_ID"],
        FoundryApiKey: c["FOUNDRY_API_KEY"],
        KeyVaultUri: c["KEYVAULT_URI"],
        RegulatoryParallelism: int.TryParse(c["REGULATORY_PARALLELISM"], out var p) ? p : 4,
        ModelProvider: c["MODEL_PROVIDER"] ?? "anthropic",
        OpenAiDeployment: c["OPENAI_DEPLOYMENT"] ?? "gpt-5-mini",
        OpenAiEndpoint: c["OPENAI_ENDPOINT"] ?? "",
        SearchProxyEndpoint: c["SEARCH_PROXY_ENDPOINT"] ?? "",
        SearchProxyAudience: c["SEARCH_PROXY_AUDIENCE"] ?? "",
        // The operator kill switch. Default ON, but an empty endpoint disables it anyway (see Program.cs) —
        // so a deployment that has not been given a proxy simply never searches the web, rather than failing.
        WebSearchEnabled: !bool.TryParse(c["WEB_SEARCH_ENABLED"], out var we) || we,
        WebSearchMaxPerStage: int.TryParse(c["WEB_SEARCH_MAX_PER_STAGE"], out var wm) ? wm : 8,
        // Default to the built-in hosted tool; set WEB_SEARCH_PROVIDER=proxy to re-enable the anonymizing egress.
        WebSearchProvider: c["WEB_SEARCH_PROVIDER"] ?? "hosted")
    { IntakeSessionContainer = c["INTAKE_SESSION_CONTAINER"] ?? "intake-sessions" };
}
