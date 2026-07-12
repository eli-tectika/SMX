using Microsoft.Extensions.Configuration;

namespace Smx.Infrastructure;

public sealed record BackendOptions(
    string FoundryEndpoint,
    string ClaudeDeployment,
    string CosmosAccountEndpoint,
    string CosmosDatabase,
    string RecordContainer,
    string LeaseContainer,
    string CompatibilityContainer,
    string CatalogContainer,
    string LearnedConclusionsContainer,
    string MarkerLibraryContainer,
    string MsdsRegistryContainer,
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
    string OpenAiEndpoint)
{
    public string AnthropicBaseUrl => $"{FoundryEndpoint.TrimEnd('/')}/anthropic/v1";

    /// Azure OpenAI endpoint for the "openai" provider. Falls back to the Foundry account endpoint
    /// (an AIServices account serves OpenAI there too) when OPENAI_ENDPOINT is not set.
    public string ResolvedOpenAiEndpoint => string.IsNullOrEmpty(OpenAiEndpoint) ? FoundryEndpoint : OpenAiEndpoint;

    // FOUNDRY_ENDPOINT / SEARCH_ENDPOINT default to "" (the API host doesn't use them);
    // the components that actually need them throw — see FoundryChatClientFactory and the
    // orchestrator Program.cs guard in Task 13.
    public static BackendOptions From(IConfiguration c) => new(
        FoundryEndpoint: c["FOUNDRY_ENDPOINT"] ?? "",
        ClaudeDeployment: c["CLAUDE_DEPLOYMENT"] ?? "claude-opus-4-7",
        CosmosAccountEndpoint: c["COSMOS_ACCOUNT_ENDPOINT"] ?? throw new InvalidOperationException("COSMOS_ACCOUNT_ENDPOINT missing"),
        CosmosDatabase: c["COSMOS_DATABASE"] ?? "smx",
        RecordContainer: c["RECORD_CONTAINER"] ?? "record",
        LeaseContainer: c["RECORD_LEASE_CONTAINER"] ?? "record-leases",
        CompatibilityContainer: c["COMPATIBILITY_CONTAINER"] ?? "ref-compatibility",
        CatalogContainer: c["CATALOG_CONTAINER"] ?? "ref-catalog",
        LearnedConclusionsContainer: c["LEARNED_CONCLUSIONS_CONTAINER"] ?? "learned-conclusions",
        MarkerLibraryContainer: c["MARKER_LIBRARY_CONTAINER"] ?? "marker-library",
        MsdsRegistryContainer: c["MSDS_REGISTRY_CONTAINER"] ?? "msds-registry",
        SearchEndpoint: c["SEARCH_ENDPOINT"] ?? "",
        SdsIndex: c["SDS_SEARCH_INDEX"] ?? "sds-index",
        ReferenceIndex: c["REFERENCE_SEARCH_INDEX"] ?? "smx-reference",
        RegulatoryIndex: c["REGULATORY_SEARCH_INDEX"] ?? "regulatory-index",
        LearnedConclusionsIndex: c["LEARNED_CONCLUSIONS_SEARCH_INDEX"] ?? "learned-conclusions",
        UamiClientId: c["UAMI_CLIENT_ID"],
        FoundryApiKey: c["FOUNDRY_API_KEY"],
        KeyVaultUri: c["KEYVAULT_URI"],
        RegulatoryParallelism: int.TryParse(c["REGULATORY_PARALLELISM"], out var p) ? p : 4,
        ModelProvider: c["MODEL_PROVIDER"] ?? "anthropic",
        OpenAiDeployment: c["OPENAI_DEPLOYMENT"] ?? "gpt-5-mini",
        OpenAiEndpoint: c["OPENAI_ENDPOINT"] ?? "");
}
