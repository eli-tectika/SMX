targetScope = 'resourceGroup'

@description('Short workload token used in resource names.')
param namePrefix string = 'smx'

@allowed(['dev', 'prod'])
@description('Environment label used in resource names and SKUs.')
param env string = 'prod'

@description('Azure region for all resources (defaults to Sweden Central, not the RG location, so AI models are available).')
param location string = 'swedencentral'

@description('Short region token used in resource names.')
param regionShort string = 'swc'

@description('Public IP of the deploying machine, allowlisted on service firewalls during deployment.')
param deployerIpAddress string = ''

@allowed(['Enabled', 'Disabled'])
@description('Public network access for data/AI services at deploy time. harden.sh flips these to Disabled post-deploy.')
param publicNetworkAccess string = 'Enabled'

@description('Cosmos SQL database name.')
param cosmosDatabaseName string = 'smx'

@description('Deploy the gpt-4o chat model (requires Standard gpt-4o quota; OFF by default).')
param deployGpt4o bool = false

@description('Deploy the Claude Opus 4.7 reasoning model on Foundry (Anthropic, GlobalStandard). ON by default — the agent backend needs it.')
param deployClaude bool = true

@description('Deploy the gpt-5-mini chat model — the stand-in the agents run on when Claude is off. ON by default so the account always offers a chat model.')
param deployGpt5Mini bool = true

// The agents call Claude when it was deployed, and the gpt-5-mini stand-in otherwise. DERIVED, not a
// parameter, so the two can never contradict each other: a deploy that passed deployClaude=false while the
// app defaulted to the Anthropic provider is exactly how every agent turn came to die on a 404
// `api_not_supported` — an account with no Anthropic deployment does not serve /anthropic at all.
var modelProvider = deployClaude ? 'anthropic' : 'openai'

@description('Frontend SPA image (ACR path incl. tag). Empty = placeholder.')
param frontendImage string = ''

@description('Backend API image (ACR path incl. tag). Empty = placeholder.')
param backendImage string = ''

@description('Orchestrator image (ACR path incl. tag). Empty = placeholder.')
param orchestratorImage string = ''

@description('Entra app-registration client id for Function App Easy Auth. Empty = auth OFF (first deploy); configure-auth.sh fills it in.')
param authClientId string = ''

@description('API app registration client id (audience the backend validates). Empty = backend auth OFF; configure-auth.sh produces it.')
param apiClientId string = ''

@description('Entra app-registration client id for the Search Proxy — its OWN registration, never regsync\'s. Empty = auth OFF (first deploy); configure-auth.sh fills it in.')
param proxyAuthClientId string = ''

@description('Key Vault secret URI holding the search provider API key (set-search-key.sh prints it). Empty = the proxy answers 503.')
param proxySearchKeySecretUri string = ''

@description('Grant the proxy identity read on the search-key secret. Leave false on a fresh subscription — the secret does not exist yet; set-search-key.sh creates it, then redeploy with true.')
param deploySearchKeyRbac bool = false

@description('Run the Search Proxy with no egress and no API key (dry-run twin).')
param proxyDryRun bool = false

@description('Operator kill switch for the Discovery agent\'s external web search.')
param webSearchEnabled bool = true

@description('Registered domain / Azure DNS zone (empty = skip DNS record management).')
param appDomainName string = ''

@description('Versionless Key Vault secret ID of the gateway TLS cert (empty = HTTP-only).')
// Not a secret value — a Key Vault resource identifier (same shape as proxySearchKeySecretUri).
#disable-next-line secure-secrets-in-params
param certKeyVaultSecretId string = ''

@description('Principal id of the KeyVault-Acmebot managed identity, deployed separately by the operator (empty = skip its DNS-01 + Key Vault role grants).')
param acmebotPrincipalId string = ''

@description('Extra tags merged onto every resource.')
param tags object = {}

var uniqueSuffix = take(uniqueString(resourceGroup().id, namePrefix), 5)
var mergedTags = union({ project: 'SMX', managedBy: 'bicep', environment: env }, tags)
var searchSku = env == 'prod' ? 'standard' : 'basic'

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
  }
}

module security 'modules/security.bicep' = {
  name: 'security'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    acmebotPrincipalId: acmebotPrincipalId
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: mergedTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    cosmosDatabaseName: cosmosDatabaseName
  }
}

module ai 'modules/ai.bicep' = {
  name: 'ai'
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: mergedTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    searchSku: searchSku
    deployGpt4o: deployGpt4o
    deployClaude: deployClaude
    deployGpt5Mini: deployGpt5Mini
  }
}

module privateEndpoints 'modules/privateendpoints.bicep' = {
  name: 'privateEndpoints'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
    peSubnetId: network.outputs.peSubnetId
    storageId: data.outputs.storageId
    cosmosId: data.outputs.cosmosId
    searchId: ai.outputs.searchId
    foundryId: ai.outputs.foundryId
    keyVaultId: security.outputs.keyVaultId
    dnsZoneBlob: network.outputs.dnsZoneBlob
    dnsZoneDfs: network.outputs.dnsZoneDfs
    dnsZoneCosmos: network.outputs.dnsZoneCosmos
    dnsZoneSearch: network.outputs.dnsZoneSearch
    dnsZoneOpenai: network.outputs.dnsZoneOpenai
    dnsZoneCognitive: network.outputs.dnsZoneCognitive
    dnsZoneServicesAi: network.outputs.dnsZoneServicesAi
    dnsZoneVault: network.outputs.dnsZoneVault
    spStorageId: functions.outputs.spStorageId
    rsStorageId: functions.outputs.rsStorageId
    searchProxyAppId: functions.outputs.searchProxyAppId
    regSyncAppId: functions.outputs.regSyncAppId
    dnsZoneQueue: network.outputs.dnsZoneQueue
    dnsZoneTable: network.outputs.dnsZoneTable
    dnsZoneSites: network.outputs.dnsZoneSites
  }
}

// ---------------- Plan 3: compute, functions, gateway ----------------

module acr 'modules/acr.bicep' = {
  name: 'acr'
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: mergedTags
    uniqueSuffix: uniqueSuffix
    acrSku: env == 'prod' ? 'Premium' : 'Standard'
    uamiPrincipalId: security.outputs.uamiPrincipalId
  }
}

module compute 'modules/compute.bicep' = {
  name: 'compute'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
    acaSubnetId: network.outputs.acaSubnetId
    uamiId: security.outputs.uamiId
    includeDedicatedProfile: env == 'prod'
    acrLoginServer: acr.outputs.acrLoginServer
    frontendImage: frontendImage
    backendImage: backendImage
    orchestratorImage: orchestratorImage
    uamiClientId: security.outputs.uamiClientId
    foundryEndpoint: ai.outputs.foundryEndpoint
    modelProvider: modelProvider
    cosmosEndpoint: data.outputs.cosmosDocumentEndpoint
    searchEndpoint: 'https://${ai.outputs.searchName}.search.windows.net'
    keyVaultUri: security.outputs.keyVaultUri
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    // The orchestrator reaches the proxy over its private endpoint; nothing here consumes compute's
    // outputs, so this dependency on functions does not close a cycle.
    searchProxyEndpoint: 'https://${functions.outputs.searchProxyDefaultHostName}'
    searchProxyAudience: empty(proxyAuthClientId) ? '' : 'api://${proxyAuthClientId}'
    webSearchEnabled: webSearchEnabled
    entraTenantId: empty(apiClientId) ? '' : tenant().tenantId
    apiClientId: apiClientId
  }
}

module functions 'modules/functions.bicep' = {
  name: 'functions'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
    uniqueSuffix: uniqueSuffix
    functionsSubnetId: network.outputs.functionsSubnetId
    workloadUamiId: security.outputs.uamiId
    workloadUamiClientId: security.outputs.uamiClientId
    workloadUamiPrincipalId: security.outputs.uamiPrincipalId
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    publicNetworkAccess: publicNetworkAccess
    deployerIpAddress: deployerIpAddress
    cosmosAccountEndpoint: 'https://${data.outputs.cosmosName}.documents.azure.com:443/'
    cosmosDatabaseName: cosmosDatabaseName
    bronzeAccountName: data.outputs.storageName
    searchEndpoint: 'https://${ai.outputs.searchName}.search.windows.net'
    foundryEndpoint: ai.outputs.foundryEndpoint
    authClientId: authClientId
    proxyAuthClientId: proxyAuthClientId
    proxySearchKeySecretUri: proxySearchKeySecretUri
    keyVaultName: security.outputs.keyVaultName
    deploySearchKeyRbac: deploySearchKeyRbac
    proxyDryRun: proxyDryRun
  }
}

module gateway 'modules/gateway.bicep' = {
  name: 'gateway'
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: mergedTags
    agwSubnetId: network.outputs.agwSubnetId
    acaStaticIp: compute.outputs.envStaticIp
    acaDefaultDomain: compute.outputs.envDefaultDomain
    frontendFqdn: compute.outputs.frontendFqdn
    backendFqdn: compute.outputs.backendFqdn
    // Single-RG variant: one VNet holds both the gateway and the ACA env.
    dnsVnetLinks: [
      { name: 'main', vnetId: network.outputs.vnetId }
    ]
    gatewaySku: env == 'prod' ? 'WAF_v2' : 'Standard_v2'
    uamiId: security.outputs.uamiId
    certKeyVaultSecretId: certKeyVaultSecretId
    // Gives the gateway a real hostname instead of a bare IP. uniqueSuffix keeps the label
    // globally unique within the region, which cloudapp.azure.com requires.
    dnsLabel: '${namePrefix}-${env}-${uniqueSuffix}'
  }
}

// App domain A record → the App Gateway's public IP. The DNS zone itself is created out-of-band
// by an Azure App Service Domain purchase (operator step); this module only manages the record,
// and is gated off entirely until appDomainName is set. Single-RG variant: no hub RG here, so the
// zone (and record) live in this same resource group — no explicit scope needed.
module dns 'modules/dns.bicep' = if (!empty(appDomainName)) {
  name: 'dns'
  params: {
    zoneName: appDomainName
    recordName: env // 'dev' → dev.<domain>
    gatewayIp: gateway.outputs.gatewayPublicIp
    acmebotPrincipalId: acmebotPrincipalId
  }
}

// Audit-only governance guardrails over this RG's PaaS (public-access audits; SMX-009).
// Gated: policyAssignments/write needs the Resource Policy Contributor role (see infra/main.bicep).
param deployPolicyGuardrails bool = true
module policy 'modules/policy.bicep' = if (deployPolicyGuardrails) {
  name: 'policy-${env}'
}

output resourceGroupName string = resourceGroup().name
output uniqueSuffix string = uniqueSuffix
output storageName string = data.outputs.storageName
output cosmosName string = data.outputs.cosmosName
output searchName string = ai.outputs.searchName
output keyVaultName string = security.outputs.keyVaultName
output foundryEndpoint string = ai.outputs.foundryEndpoint
output acrLoginServer string = acr.outputs.acrLoginServer
output frontendFqdn string = compute.outputs.frontendFqdn
output gatewayPublicIp string = gateway.outputs.gatewayPublicIp
output gatewayFqdn string = gateway.outputs.gatewayFqdn
output gatewayUrl string = 'http://${gateway.outputs.gatewayFqdn}'
output searchProxyAppName string = functions.outputs.searchProxyAppName
output regSyncAppName string = functions.outputs.regSyncAppName
