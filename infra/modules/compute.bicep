@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the ACA infrastructure subnet (delegated to Microsoft.App/environments).')
param acaSubnetId string

@description('Resource ID of the workload user-assigned managed identity.')
param uamiId string

@description('Placeholder image until real app images exist; swapped via swap-images.sh.')
param placeholderImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Add a Dedicated (D4) workload profile (prod).')
param includeDedicatedProfile bool = false

@description('ACR login server (empty = no registry wiring; placeholder images only).')
param acrLoginServer string = ''

@description('Frontend SPA image (empty = placeholder).')
param frontendImage string = ''

@description('Backend API image (empty = placeholder).')
param backendImage string = ''

@description('Orchestrator image (empty = placeholder).')
param orchestratorImage string = ''

@description('Client ID of the workload UAMI (env var for ManagedIdentityCredential).')
param uamiClientId string = ''

@description('Foundry account endpoint; the app derives the /anthropic/v1 base itself.')
param foundryEndpoint string = ''

@description('Which model the agents call: "anthropic" (Claude on Foundry) or "openai" (the gpt-5-mini stand-in). main.bicep derives this from deployClaude so it cannot name a model that was never deployed.')
@allowed([
  'anthropic'
  'openai'
])
param modelProvider string = 'anthropic'

@description('Claude deployment name; used only when modelProvider is "anthropic". Must match the deployment ai.bicep creates.')
param claudeDeployment string = 'claude-opus-4-7'

@description('OpenAI chat deployment name; used only when modelProvider is "openai". Must match the deployment ai.bicep creates.')
param openAiDeployment string = 'gpt-5-mini'

@description('Cosmos account document endpoint.')
param cosmosEndpoint string = ''

@description('AI Search endpoint.')
param searchEndpoint string = ''

@description('App Insights connection string (empty = telemetry off).')
param appInsightsConnectionString string = ''

@description('Key Vault URI for the Foundry Anthropic key fallback (empty = Entra-only).')
param keyVaultUri string = ''

@description('Search Proxy base URL (https://<app>.azurewebsites.net), reached over its private endpoint.')
param searchProxyEndpoint string = ''

@description('Entra audience of the Search Proxy (api://<proxyAuthClientId>). Web search is gated on SEARCH_PROXY_ENDPOINT, not this: with an endpoint set but no audience the tool is ON but every call fails safe at token acquisition.')
param searchProxyAudience string = ''

@description('Operator kill switch for external web search, and its per-stage query budget.')
param webSearchEnabled bool = true
param webSearchMaxPerStage int = 8

@description('Entra tenant id for JwtBearer (empty = backend auth OFF).')
param entraTenantId string = ''

@description('API app registration client id = the audience the backend validates (empty = auth OFF).')
param apiClientId string = ''

var caeName = 'cae-${namePrefix}-${env}-${regionShort}'
var consumptionProfile = [
  {
    name: 'Consumption'
    workloadProfileType: 'Consumption'
  }
]
var dedicatedProfile = [
  {
    name: 'D4'
    workloadProfileType: 'D4'
    minimumCount: 1
    maximumCount: 3
  }
]

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: caeName
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: acaSubnetId
      internal: true
    }
    workloadProfiles: includeDedicatedProfile ? concat(consumptionProfile, dedicatedProfile) : consumptionProfile
    zoneRedundant: false
  }
}

var sharedEnv = [
  { name: 'UAMI_CLIENT_ID', value: uamiClientId }
  { name: 'FOUNDRY_ENDPOINT', value: foundryEndpoint }
  { name: 'COSMOS_ACCOUNT_ENDPOINT', value: cosmosEndpoint }
  { name: 'SEARCH_ENDPOINT', value: searchEndpoint }
  { name: 'KEYVAULT_URI', value: keyVaultUri }
  // Which model the agents actually call. This is passed in rather than defaulted in code, and main.bicep
  // derives it from `deployClaude`, because the two drifting apart is not a hypothetical: the code defaults
  // to 'anthropic', a deploy passed deployClaude=false, and every agent turn then died on a 404
  // `api_not_supported` — the /anthropic API surface is not even enabled on an account with no Anthropic
  // deployment. Deriving the provider from what was deployed makes that combination unrepresentable.
  { name: 'MODEL_PROVIDER', value: modelProvider }
  { name: 'CLAUDE_DEPLOYMENT', value: claudeDeployment }
  { name: 'OPENAI_DEPLOYMENT', value: openAiDeployment }
  // Both sides of the learned-conclusions loop (query embedding + document push) resolve the model from
  // this one setting, so they cannot drift apart. Must stay text-embedding-3-large: ai.bicep deploys
  // exactly that (unconditionally), and the index's vector field is sized to its 3072 dims.
  { name: 'EMBEDDING_DEPLOYMENT', value: 'text-embedding-3-large' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'LEARNED_CONCLUSIONS_CONTAINER', value: 'learned-conclusions' }
  { name: 'MARKER_LIBRARY_CONTAINER', value: 'marker-library' }
  { name: 'MSDS_REGISTRY_CONTAINER', value: 'msds-registry' }
  { name: 'SUBSTANCE_PROPERTIES_CONTAINER', value: 'substance-properties' }
  { name: 'LEARNED_CONCLUSIONS_SEARCH_INDEX', value: 'learned-conclusions' }
]

// Only the orchestrator hosts the Discovery agent's search_web tool, so only it is told where the proxy is:
// the API has no reason to hold an audience for the one component that egresses to the public internet.
var orchestratorEnv = concat(sharedEnv, [
  { name: 'SEARCH_PROXY_ENDPOINT', value: searchProxyEndpoint }
  { name: 'SEARCH_PROXY_AUDIENCE', value: searchProxyAudience }
  { name: 'WEB_SEARCH_ENABLED', value: string(webSearchEnabled) }
  { name: 'WEB_SEARCH_MAX_PER_STAGE', value: string(webSearchMaxPerStage) }
])

var registries = empty(acrLoginServer) ? [] : [
  {
    server: acrLoginServer
    identity: uamiId
  }
]

// name/image/port/ingress/env per app; probes only where there is an HTTP surface.
var apps = [
  {
    name: 'frontend'
    image: empty(frontendImage) ? placeholderImage : frontendImage
    hasIngress: true
    targetPort: 80 // nginx and the placeholder both listen on 80
    minReplicas: 1 // gateway backend probe needs a warm replica
    env: []
    probes: []
  }
  {
    name: 'backend'
    image: empty(backendImage) ? placeholderImage : backendImage
    hasIngress: true
    targetPort: empty(backendImage) ? 80 : 8080 // aspnet:8.0 default port
    minReplicas: 0
    // PATH_BASE makes the API serve under /api (App Gateway forwards /api/* unstripped).
    env: concat(sharedEnv, [
      { name: 'PATH_BASE', value: '/api' }
      { name: 'ENTRA_TENANT_ID', value: entraTenantId }
      { name: 'API_CLIENT_ID', value: apiClientId }
    ])
    probes: empty(backendImage) ? [] : [
      {
        type: 'Readiness'
        httpGet: { path: '/api/healthz', port: 8080 } // matches PATH_BASE
        initialDelaySeconds: 5
        periodSeconds: 10
      }
    ]
  }
  {
    name: 'orchestrator'
    image: empty(orchestratorImage) ? placeholderImage : orchestratorImage
    hasIngress: empty(orchestratorImage) // placeholder needs ingress to be healthy; real worker has none
    targetPort: 80
    minReplicas: empty(orchestratorImage) ? 0 : 1 // change-feed processor must be running to dispatch
    env: orchestratorEnv
    probes: []
  }
]

resource containerApps 'Microsoft.App/containerApps@2024-03-01' = [for app in apps: {
  name: 'ca-${namePrefix}-${env}-${app.name}-${regionShort}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }
  properties: {
    managedEnvironmentId: cae.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: app.hasIngress ? {
        // On an INTERNAL environment, external:true means "Limited to VNet" (registered on the
        // env's internal-LB envoy listener) — still zero public exposure; the App Gateway stays
        // the only public entry. external:false would mean "Limited to Container Apps Environment"
        // (app-to-app only): envoy's VNet-facing listener returns 404 "does not exist" to the
        // gateway for every Host form, which surfaced as a permanent 502.
        external: true
        targetPort: app.targetPort
        transport: 'auto'
        allowInsecure: true // HTTP end-to-end inside the private VNet; HTTPS deferred (Decision F)
      } : null
      registries: registries
    }
    template: {
      containers: [
        {
          name: app.name
          image: app.image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: app.env
          probes: app.probes
        }
      ]
      scale: {
        minReplicas: app.minReplicas
        maxReplicas: 2
      }
    }
  }
}]

output envId string = cae.id
output envStaticIp string = cae.properties.staticIp
output envDefaultDomain string = cae.properties.defaultDomain
output frontendFqdn string = containerApps[0].properties.configuration.ingress.fqdn
output frontendAppName string = containerApps[0].name
output backendAppName string = containerApps[1].name
output orchestratorAppName string = containerApps[2].name
output backendFqdn string = containerApps[1].properties.configuration.ingress.fqdn
