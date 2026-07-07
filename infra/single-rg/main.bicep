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
    frontendFqdn: compute.outputs.frontendFqdn
    gatewaySku: env == 'prod' ? 'WAF_v2' : 'Standard_v2'
  }
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
output searchProxyAppName string = functions.outputs.searchProxyAppName
output regSyncAppName string = functions.outputs.regSyncAppName
