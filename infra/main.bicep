targetScope = 'subscription'

@description('Short workload token used in resource names.')
param namePrefix string = 'smx'

@allowed(['dev', 'prod'])
@description('Environment to deploy.')
param env string

@description('Azure region for all resources.')
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

@description('Frontend SPA image (ACR path incl. tag). Empty = placeholder.')
param frontendImage string = ''

@description('Backend API image (ACR path incl. tag). Empty = placeholder.')
param backendImage string = ''

@description('Orchestrator image (ACR path incl. tag). Empty = placeholder.')
param orchestratorImage string = ''

@description('Entra app-registration client id for Function App Easy Auth. Empty = auth OFF (first deploy); configure-auth.sh fills it in.')
param authClientId string = ''

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

@description('Extra tags merged onto every resource.')
param tags object = {}

var uniqueSuffix = take(uniqueString(subscription().id, namePrefix), 5)
var hubRgName = 'rg-${namePrefix}-hub-${regionShort}'
var envRgName = 'rg-${namePrefix}-${env}-${regionShort}'

var baseTags = union({ project: 'SMX', managedBy: 'bicep' }, tags)
var hubTags = union(baseTags, { environment: 'shared' })
var envTags = union(baseTags, { environment: env })

var spokeCidr = env == 'prod' ? '10.2.0.0/20' : '10.1.0.0/20'
var acaSubnetCidr = env == 'prod' ? '10.2.0.0/23' : '10.1.0.0/23'
var functionsSubnetCidr = env == 'prod' ? '10.2.2.0/24' : '10.1.2.0/24'
var peSubnetCidr = env == 'prod' ? '10.2.3.0/24' : '10.1.3.0/24'

var searchSku = env == 'prod' ? 'standard' : 'basic'

// Private DNS zones live in the hub RG (created in Plan 1). At subscription scope,
// resourceId needs the 4-arg form (subscriptionId, resourceGroup, type, name).
var subId = subscription().subscriptionId
var dnsZoneBlob = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.blob.core.windows.net')
var dnsZoneDfs = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.dfs.core.windows.net')
var dnsZoneCosmos = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.documents.azure.com')
var dnsZoneSearch = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.search.windows.net')
var dnsZoneOpenai = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.openai.azure.com')
var dnsZoneCognitive = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.cognitiveservices.azure.com')
var dnsZoneServicesAi = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.services.ai.azure.com')
var dnsZoneVault = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.vaultcore.azure.net')
var dnsZoneQueue = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.queue.core.windows.net')
var dnsZoneTable = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.table.core.windows.net')
var dnsZoneSites = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.azurewebsites.net')

resource hubRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: hubRgName
  location: location
  tags: hubTags
}

resource envRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: envRgName
  location: location
  tags: envTags
}

module hub 'modules/hub.bicep' = {
  name: 'hub'
  scope: hubRg
  params: {
    namePrefix: namePrefix
    regionShort: regionShort
    location: location
    tags: hubTags
  }
}

module spoke 'modules/networking.bicep' = {
  name: 'spoke-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    spokeCidr: spokeCidr
    acaSubnetCidr: acaSubnetCidr
    functionsSubnetCidr: functionsSubnetCidr
    peSubnetCidr: peSubnetCidr
    hubVnetId: hub.outputs.vnetId
  }
}

module hubPeering 'modules/hubPeering.bicep' = {
  name: 'hub-peering-${env}'
  scope: hubRg
  params: {
    hubVnetName: hub.outputs.vnetName
    spokeVnetId: spoke.outputs.vnetId
    spokeVnetName: spoke.outputs.vnetName
  }
}

module dnsLinks 'modules/dnsLinks.bicep' = {
  name: 'dns-links-${env}'
  scope: hubRg
  params: {
    privateDnsZoneNames: hub.outputs.privateDnsZoneNames
    spokeVnetId: spoke.outputs.vnetId
    linkName: '${namePrefix}-${env}'
  }
}

// ---------------- Plan 2: security, data, AI, private endpoints ----------------

module security 'modules/security.bicep' = {
  name: 'security-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
  }
}

module data 'modules/data.bicep' = {
  name: 'data-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    cosmosDatabaseName: cosmosDatabaseName
  }
}

module ai 'modules/ai.bicep' = {
  name: 'ai-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    searchSku: searchSku
    deployGpt4o: deployGpt4o
    deployClaude: deployClaude
  }
}

module privateEndpoints 'modules/privateendpoints.bicep' = {
  name: 'pe-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    peSubnetId: spoke.outputs.peSubnetId
    storageId: data.outputs.storageId
    cosmosId: data.outputs.cosmosId
    searchId: ai.outputs.searchId
    foundryId: ai.outputs.foundryId
    keyVaultId: security.outputs.keyVaultId
    dnsZoneBlob: dnsZoneBlob
    dnsZoneDfs: dnsZoneDfs
    dnsZoneCosmos: dnsZoneCosmos
    dnsZoneSearch: dnsZoneSearch
    dnsZoneOpenai: dnsZoneOpenai
    dnsZoneCognitive: dnsZoneCognitive
    dnsZoneServicesAi: dnsZoneServicesAi
    dnsZoneVault: dnsZoneVault
    spStorageId: functions.outputs.spStorageId
    rsStorageId: functions.outputs.rsStorageId
    searchProxyAppId: functions.outputs.searchProxyAppId
    regSyncAppId: functions.outputs.regSyncAppId
    dnsZoneQueue: dnsZoneQueue
    dnsZoneTable: dnsZoneTable
    dnsZoneSites: dnsZoneSites
  }
}

// ---------------- Plan 3: compute, functions, gateway ----------------

module acr 'modules/acr.bicep' = {
  name: 'acr-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    acrSku: env == 'prod' ? 'Premium' : 'Standard'
    uamiPrincipalId: security.outputs.uamiPrincipalId
  }
}

module compute 'modules/compute.bicep' = {
  name: 'compute-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    acaSubnetId: spoke.outputs.acaSubnetId
    uamiId: security.outputs.uamiId
    includeDedicatedProfile: env == 'prod'
    acrLoginServer: acr.outputs.acrLoginServer
    frontendImage: frontendImage
    backendImage: backendImage
    orchestratorImage: orchestratorImage
    uamiClientId: security.outputs.uamiClientId
    foundryEndpoint: ai.outputs.foundryEndpoint
    cosmosEndpoint: data.outputs.cosmosDocumentEndpoint
    searchEndpoint: 'https://${ai.outputs.searchName}.search.windows.net'
    keyVaultUri: security.outputs.keyVaultUri
    appInsightsConnectionString: hub.outputs.appInsightsConnectionString
    // The orchestrator reaches the proxy over its private endpoint; nothing here consumes compute's
    // outputs, so this dependency on functions does not close a cycle.
    searchProxyEndpoint: 'https://${functions.outputs.searchProxyDefaultHostName}'
    searchProxyAudience: empty(proxyAuthClientId) ? '' : 'api://${proxyAuthClientId}'
    webSearchEnabled: webSearchEnabled
  }
}

module functions 'modules/functions.bicep' = {
  name: 'functions-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    functionsSubnetId: spoke.outputs.functionsSubnetId
    workloadUamiId: security.outputs.uamiId
    workloadUamiClientId: security.outputs.uamiClientId
    workloadUamiPrincipalId: security.outputs.uamiPrincipalId
    appInsightsConnectionString: hub.outputs.appInsightsConnectionString
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
  name: 'gateway-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    agwSubnetId: '${hub.outputs.vnetId}/subnets/snet-agw-${env}'
    acaStaticIp: compute.outputs.envStaticIp
    acaDefaultDomain: compute.outputs.envDefaultDomain
    frontendFqdn: compute.outputs.frontendFqdn
    backendFqdn: compute.outputs.backendFqdn
    // Link the ACA private DNS zone to the hub VNet (where the gateway resolves names) and the spoke.
    dnsVnetLinks: [
      { name: 'hub', vnetId: hub.outputs.vnetId }
      { name: 'spoke', vnetId: spoke.outputs.vnetId }
    ]
    gatewaySku: env == 'prod' ? 'WAF_v2' : 'Standard_v2'
  }
}

// Audit-only governance guardrails over the spoke's PaaS (public-access audits; SMX-009).
// Gated: policyAssignments/write needs the Resource Policy Contributor role, which the current
// dev deployer account lacks (mirrors the deployClaude quota gate). Default on for fresh
// subscriptions (deployers are typically Owner); dev.bicepparam sets it false until granted.
param deployPolicyGuardrails bool = true
module policy 'modules/policy.bicep' = if (deployPolicyGuardrails) {
  name: 'policy-${env}'
  scope: envRg
}

output hubResourceGroup string = hubRg.name
output envResourceGroup string = envRg.name
output uniqueSuffix string = uniqueSuffix
output hubVnetId string = hub.outputs.vnetId
output spokeVnetId string = spoke.outputs.vnetId
output uamiClientId string = security.outputs.uamiClientId
output keyVaultName string = security.outputs.keyVaultName
output storageName string = data.outputs.storageName
output cosmosName string = data.outputs.cosmosName
output searchName string = ai.outputs.searchName
output foundryEndpoint string = ai.outputs.foundryEndpoint
output acrLoginServer string = acr.outputs.acrLoginServer
output frontendFqdn string = compute.outputs.frontendFqdn
output gatewayPublicIp string = gateway.outputs.gatewayPublicIp
output searchProxyAppName string = functions.outputs.searchProxyAppName
output regSyncAppName string = functions.outputs.regSyncAppName
